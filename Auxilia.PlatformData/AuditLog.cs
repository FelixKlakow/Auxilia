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
/// Append-only writer for the platform audit log. Platform components are the only writers;
/// workflows can neither write nor read audit entries.
/// </summary>
public sealed class AuditLog : IAsyncDisposable
{
    private readonly IDataAccess<AuditRecord> _dataAccess;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<(AuditRecord Record, TaskCompletionSource? Flush)>? _queue;
    private readonly Task? _writer;

    public AuditLog(
        IDataAccess<AuditRecord> dataAccess, TimeProvider timeProvider,
        AuditLogSettings? settings = null)
    {
        _dataAccess = dataAccess;
        _timeProvider = timeProvider;
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

    public async Task AppendAsync(
        string actor, string action, string subject, string outcome,
        string? detailJson = null, CancellationToken ct = default)
    {
        var record = new AuditRecord
        {
            TimestampUtc = _timeProvider.GetUtcNow(),
            Actor = actor,
            Action = action,
            Subject = subject,
            Outcome = outcome,
            DetailJson = detailJson
        };

        if (_queue is null)
        {
            await _dataAccess.SaveAsync(record, ct);
            return;
        }
        await _queue.Writer.WriteAsync((record, null), ct);
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

            // The store being down means the whole node is failing; retry briefly so a
            // transient hiccup loses nothing, then surrender that one record rather than
            // wedging the audit pipeline forever.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _dataAccess.SaveAsync(record);
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
