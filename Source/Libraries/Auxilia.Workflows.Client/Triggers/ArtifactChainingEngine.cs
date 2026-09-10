using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.Workflows.Client.Triggers;

/// <summary>
/// The embeddable artifact-chaining engine: consumes the Core's SERVER-SIDE-FILTERED artifact
/// SSE stream (one consumer per distinct artifact type among the enabled triggers — never the
/// global feed, never the message bus) and dispatches each matching trigger's follow-up
/// workflow with the artifact reference in its context. Triggers are re-read from the store
/// per event, so edits apply without a restart; ADDING a trigger for a NEW artifact type needs
/// <see cref="RefreshAsync"/> (or a host restart) to open its stream.
/// Reconnect lives INSIDE <see cref="ICoreClient"/>; on every reconnected frame this engine
/// CATCHES UP via <see cref="ICoreClient.QueryArtifactsAsync"/> (artifacts persisted during the
/// disconnect window are not replayed by the stream) and dedupes against double delivery. The
/// catch-up cursor is seeded from the host clock when a consumer starts, so a drop BEFORE the
/// first live event still defines a gap (assumes host/Core clocks agree to within
/// <see cref="CatchUpOverlap"/>). Only the engine's own token stops a consumer; a failing
/// dispatch, store read, or unary timeout is logged and the stream continues.
/// </summary>
public sealed class ArtifactChainingEngine(
    ITriggerStore store,
    ICoreClient core,
    TimeProvider timeProvider,
    ILogger<ArtifactChainingEngine> logger) : IHostedService, IDisposable
{
    /// <summary>Dispatched-artifact-id memory per consumer, bounding the catch-up/live dedupe.</summary>
    internal const int DedupeCapacity = 512;

    /// <summary>
    /// How far BEFORE the newest handled timestamp a catch-up re-reads: the query is strictly
    /// "created after" and stores round timestamps, so an artifact sharing the last handled
    /// millisecond would otherwise be skipped. The id dedupe absorbs the re-read.
    /// </summary>
    internal static readonly TimeSpan CatchUpOverlap = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Consumer)> _consumers = new(StringComparer.Ordinal);
    private CancellationTokenSource? _lifetime;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = new CancellationTokenSource();
        await RefreshAsync(cancellationToken);
        logger.LogInformation("ArtifactChainingEngine started with {Count} stream consumer(s).",
            _consumers.Count);
    }

    /// <summary>
    /// Aligns the stream consumers with the CURRENT enabled trigger set: opens a filtered
    /// consumer per new artifact type, stops consumers no trigger needs anymore.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_lifetime is null)
            throw new InvalidOperationException("the engine is not started");

        var wanted = (await store.GetArtifactTriggersAsync(ct))
            .Where(t => t.Enabled)
            .Select(t => t.ArtifactType)
            .ToHashSet(StringComparer.Ordinal);

        lock (_gate)
        {
            foreach (var (type, consumer) in _consumers.Where(c => !wanted.Contains(c.Key)).ToList())
            {
                consumer.Cts.Cancel();
                _consumers.Remove(type);
            }
            foreach (var type in wanted.Where(t => !_consumers.ContainsKey(t)))
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _consumers[type] = (cts, ConsumeAsync(type, cts.Token));
            }
        }
    }

    private async Task ConsumeAsync(string artifactType, CancellationToken ct)
    {
        // The client stream reconnects internally; this loop only reacts to its frames. Track the
        // newest handled CreatedUtc (seeded with the consumer's start so a drop before the first
        // live event still has a gap to query), and remember recently dispatched artifact ids so
        // catch-up overlapping the live stream dispatches once.
        var lastSeenUtc = timeProvider.GetUtcNow();
        var dispatched = new HashSet<Guid>();
        var dispatchedOrder = new Queue<Guid>();

        try
        {
            await foreach (var frame in core.StreamArtifactEventsAsync(artifactType, ct: ct))
            {
                switch (frame)
                {
                    case StreamEventFrame<ArtifactStreamEvent> evt:
                        if (!Remember(evt.Event.Artifact.Id))
                            break;
                        lastSeenUtc = Max(lastSeenUtc, evt.Event.Artifact.CreatedUtc);
                        try
                        {
                            await HandleAsync(evt.Event, ct);
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            // A failed trigger read is not a stream fault — the stream continues.
                            logger.LogError(ex,
                                "Handling artifact {ArtifactId} of '{ArtifactType}' failed — the stream continues.",
                                evt.Event.Artifact.Id, artifactType);
                        }
                        break;

                    case StreamConnectionFrame<ArtifactStreamEvent> { State: StreamConnectionState.Reconnecting } down:
                        logger.LogWarning(down.Cause,
                            "Artifact stream for '{ArtifactType}' dropped — the client retries in {Backoff}s.",
                            artifactType, down.RetryDelay?.TotalSeconds ?? 0);
                        break;

                    case StreamConnectionFrame<ArtifactStreamEvent> { State: StreamConnectionState.Connected, Attempt: > 1 }:
                        await CatchUpAsync(ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Engine stop.
        }
        catch (Exception ex)
        {
            // Non-transient (auth/permission) — retrying cannot help; surface loudly and stop
            // this consumer. RefreshAsync/restart re-opens it once the deployment is fixed.
            logger.LogError(ex,
                "Artifact stream consumer for '{ArtifactType}' stopped on a non-recoverable error.",
                artifactType);
        }
        return;

        bool Remember(Guid artifactId)
        {
            if (!dispatched.Add(artifactId))
                return false;
            dispatchedOrder.Enqueue(artifactId);
            if (dispatchedOrder.Count > DedupeCapacity)
                dispatched.Remove(dispatchedOrder.Dequeue());
            return true;
        }

        static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

        // Artifacts persisted while disconnected: page the store oldest-first under ONE fixed
        // lower bound (a bound that moves per page skips artifacts sharing a page's last
        // timestamp) and run them through the same trigger dispatch (deduped above).
        async Task CatchUpAsync(CancellationToken innerCt)
        {
            var since = lastSeenUtc - CatchUpOverlap;
            var skip = 0;
            try
            {
                while (true)
                {
                    var page = await core.QueryArtifactsAsync(
                        new ArtifactQuery(artifactType, CreatedAfterUtc: since, Skip: skip, Take: 200), innerCt);
                    foreach (var artifact in page.Items)
                    {
                        lastSeenUtc = Max(lastSeenUtc, artifact.CreatedUtc);
                        if (Remember(artifact.Id))
                            await HandleAsync(new ArtifactStreamEvent(artifact, artifact.CreatedUtc), innerCt);
                    }
                    skip += page.Items.Count;
                    if (page.Items.Count == 0 || skip >= page.Total)
                        break;
                }
                logger.LogInformation(
                    "Artifact stream for '{ArtifactType}' reconnected — catch-up complete.", artifactType);
            }
            catch (Exception ex) when (!innerCt.IsCancellationRequested)
            {
                logger.LogWarning(ex,
                    "Catch-up query for '{ArtifactType}' failed — live events continue; the gap retries on the next reconnect.",
                    artifactType);
            }
        }
    }

    /// <summary>
    /// Dispatches every enabled trigger matching the event (type + optional work-item filter).
    /// Triggers are read fresh from the store, so edits apply immediately. A failing dispatch
    /// is logged and does not stop the remaining triggers or the stream.
    /// </summary>
    public async Task HandleAsync(ArtifactStreamEvent evt, CancellationToken ct = default)
    {
        var matching = (await store.GetArtifactTriggersAsync(ct))
            .Where(t => t.Enabled)
            .Where(t => string.Equals(t.ArtifactType, evt.Artifact.ArtifactType, StringComparison.Ordinal))
            .Where(t => t.WorkItemId is null
                        || string.Equals(t.WorkItemId, evt.Artifact.WorkItemId, StringComparison.Ordinal))
            .ToList();

        foreach (var trigger in matching)
        {
            var context = new Dictionary<string, string>
            {
                ["ArtifactId"] = evt.Artifact.Id.ToString("D"),
                ["ArtifactType"] = evt.Artifact.ArtifactType,
                ["WorkItemId"] = evt.Artifact.WorkItemId
            };
            foreach (var (key, value) in trigger.Context ?? new Dictionary<string, string>())
                context.TryAdd(key, value);

            try
            {
                var accepted = await ScheduledTriggerEngine.DispatchAsync(
                    core, trigger.ConfigurationId, trigger.WorkflowType, context,
                    trigger.RunAsPrincipalId, ct);
                logger.LogInformation(
                    "Artifact trigger {TriggerId} dispatched. Artifact={ArtifactType} v{Version} RunId={RunId}",
                    trigger.Id, evt.Artifact.ArtifactType, evt.Artifact.Version, accepted.RunId);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex,
                    "Artifact trigger {TriggerId} failed to dispatch for artifact {ArtifactId}.",
                    trigger.Id, evt.Artifact.Id);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is null)
            return;
        await _lifetime.CancelAsync();
        Task[] consumers;
        lock (_gate)
        {
            consumers = _consumers.Values.Select(c => c.Consumer).ToArray();
            _consumers.Clear();
        }
        await Task.WhenAll(consumers).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
            .ContinueWith(_ => { });
    }

    public void Dispose() => _lifetime?.Dispose();
}
