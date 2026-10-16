using System.Threading.Channels;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData;

/// <summary>Audit write behaviour; the default (synchronous) suits dev and moderate load.</summary>
public sealed class AuditLogSettings
{
    /// <summary>
    /// When true, appends enqueue onto a bounded channel drained by one background writer —
    /// request paths stop serializing on the audit store at high rates. Trade: a crash can
    /// lose the not-yet-drained tail (bounded by <see cref="MaxQueued"/>); an orderly shutdown
    /// drains fully. Default false: every append is a completed store write.
    /// </summary>
    public bool BatchedWrites { get; set; }

    /// <summary>Queue bound; a full queue applies backpressure (waits), never drops.</summary>
    public int MaxQueued { get; set; } = 10_000;
}

/// <summary>
/// Receives this writer's chain head after EVERY record reached the store — the anchor kept
/// outside the store that makes truncating the trail detectable with no unanchored window.
/// </summary>
public interface IAuditAnchor
{
    Task AnchorAsync(AuditChainHead head, CancellationToken ct);
}

/// <summary>
/// Forwards each record this process writes to the platform's central audit authority (the
/// Core) — how a service with its own store (the runner) keeps the trail centralized.
/// </summary>
public interface IAuditRelay
{
    Task RelayAsync(AuditRecord record, CancellationToken ct);
}

/// <summary>
/// Append-only writer for the platform audit log. Platform components are the only writers;
/// workflows can neither write nor read audit entries. Every record is chained (sequence +
/// keyed hash over its predecessor, <see cref="AuditChain"/>) so edits, deletions and
/// insertions in the store are detectable.
/// </summary>
public sealed class AuditLog : IAsyncDisposable
{
    private readonly IDataAccess<AuditRecord> _dataAccess;
    private readonly TimeProvider _timeProvider;
    private readonly AuditIntegrity _integrity;
    private readonly IAuditRelay? _relay;
    private readonly IAuditAnchor? _anchor;
    private readonly Channel<(AuditRecord Record, TaskCompletionSource? Flush)>? _queue;
    private readonly Task? _writer;
    private readonly SemaphoreSlim _chainGate = new(1, 1);
    private readonly string _chainId = Guid.NewGuid().ToString("N");
    private long _sequence;
    private string? _previousHash;
    private AuditChainHead? _persistedHead;

    public AuditLog(
        IDataAccess<AuditRecord> dataAccess, TimeProvider timeProvider,
        AuditLogSettings? settings = null, AuditIntegrity? integrity = null, IAuditRelay? relay = null,
        IAuditAnchor? anchor = null)
    {
        _dataAccess = dataAccess;
        _timeProvider = timeProvider;
        _integrity = integrity ?? new AuditIntegrity(null);
        _relay = relay;
        _anchor = anchor;
        if (settings?.BatchedWrites != true)
            return;

        _queue = Channel.CreateBounded<(AuditRecord, TaskCompletionSource?)>(
            new BoundedChannelOptions(Math.Max(1, settings.MaxQueued))
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait // backpressure, never drop audit
            });
        _writer = Task.Run(DrainAsync);
    }

    /// <summary>The integrity key the chain is computed with (for verification).</summary>
    public AuditIntegrity Integrity => _integrity;

    /// <summary>This writer's newest record that reached the store — what an external checkpoint anchors.</summary>
    public AuditChainHead? PersistedHead => Volatile.Read(ref _persistedHead);

    public Task AppendAsync(
        string actor, string action, string subject, string outcome,
        string? detailJson = null, CancellationToken ct = default)
        => AppendRecordAsync(new AuditRecord
        {
            TimestampUtc = _timeProvider.GetUtcNow(),
            Actor = actor,
            Action = action,
            Subject = subject,
            Outcome = outcome,
            DetailJson = detailJson
        }, relay: true, ct);

    /// <summary>
    /// Appends a record another platform service recorded and forwarded (keeping its id, time
    /// and content, stamped with its <paramref name="source"/>), into THIS log's chain.
    /// Idempotent per record id — a redelivered forward is not stored twice.
    /// </summary>
    public async Task AppendForwardedAsync(AuditRecord forwarded, string source, CancellationToken ct = default)
    {
        if (await _dataAccess.ReadAsync(forwarded.Id, ct) is not null)
            return;
        await AppendRecordAsync(forwarded with
        {
            Source = source, ChainId = null, Sequence = 0, PreviousHash = null, Hash = null
        }, relay: false, ct);
    }

    private async Task AppendRecordAsync(AuditRecord record, bool relay, CancellationToken ct)
    {
        if (_queue is null)
        {
            await _chainGate.WaitAsync(ct);
            try
            {
                record = Chain(record);
                await _dataAccess.SaveAsync(record, ct);
                await PersistedAsync(record, ct);
            }
            finally
            {
                _chainGate.Release();
            }
        }
        else
        {
            await _queue.Writer.WriteAsync((record, null), ct);
        }

        if (relay && _relay is not null)
            await _relay.RelayAsync(record, ct);
    }

    /// <summary>Links the record into this writer's chain. Callers hold the chain order (gate or single drain).</summary>
    private AuditRecord Chain(AuditRecord record)
    {
        var chained = record with
        {
            ChainId = _chainId,
            Sequence = ++_sequence,
            PreviousHash = _previousHash,
            Hash = null
        };
        chained = chained with { Hash = AuditChain.Hash(_integrity.Key, chained) };
        _previousHash = chained.Hash;
        return chained;
    }

    /// <summary>Advances the stored head and anchors it (in chain order: callers hold the gate or the single drain).</summary>
    private async Task PersistedAsync(AuditRecord record, CancellationToken ct)
    {
        var head = new AuditChainHead(record.ChainId!, record.Sequence, record.Hash!);
        Volatile.Write(ref _persistedHead, head);
        if (_anchor is not null)
            await _anchor.AnchorAsync(head, ct);
    }

    /// <summary>Completes when every append made before this call has reached the store.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_queue is null)
            return;
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The sentinel rides the same single-reader queue, so it completes strictly after
        // everything enqueued before it.
        await _queue.Writer.WriteAsync((new AuditRecord
        {
            TimestampUtc = _timeProvider.GetUtcNow(),
            Actor = "audit-log",
            Action = "flush",
            Subject = string.Empty,
            Outcome = string.Empty
        }, flushed), ct);
        await flushed.Task.WaitAsync(ct);
    }

    private async Task DrainAsync()
    {
        await foreach (var (record, flush) in _queue!.Reader.ReadAllAsync())
        {
            if (flush is not null)
            {
                flush.TrySetResult();
                continue; // the sentinel is a signal, not an audit entry
            }

            // The single drain is the chain order. The store being down means the whole node is
            // failing; retry briefly so a transient hiccup loses nothing, then surrender that
            // one record rather than wedging the audit pipeline forever (the chain then shows
            // exactly that gap).
            var chained = Chain(record);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _dataAccess.SaveAsync(chained);
                    await PersistedAsync(chained, CancellationToken.None);
                    break;
                }
                catch when (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
                }
                catch
                {
                    break;
                }
            }
        }
    }

    /// <summary>Orderly shutdown: stop accepting and drain everything queued.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_queue is null)
            return;
        _queue.Writer.TryComplete();
        if (_writer is not null)
            await _writer;
    }
}
