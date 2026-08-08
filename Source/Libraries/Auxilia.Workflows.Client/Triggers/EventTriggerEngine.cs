using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Auxilia.Workflows.Client.Triggers;

/// <summary>
/// The embeddable event-trigger engine: consumes the Core's SERVER-SIDE-FILTERED platform-event
/// SSE stream (one consumer per distinct event type among the enabled triggers — never the
/// global feed, never the message bus) and dispatches each matching trigger's follow-up
/// workflow with the event reference and payload in its context. Triggers are re-read from the
/// store per event, so edits apply without a restart; ADDING a trigger for a NEW event type
/// needs <see cref="RefreshAsync"/> (or a host restart) to open its stream.
/// Reconnect lives INSIDE <see cref="ICoreClient"/>; on every reconnected frame this engine
/// CATCHES UP via <see cref="ICoreClient.QueryEventsAsync"/> (events published during the
/// disconnect window are not replayed by the stream) and dedupes against double delivery.
/// </summary>
public sealed class EventTriggerEngine(
    ITriggerStore store,
    ICoreClient core,
    ILogger<EventTriggerEngine> logger) : IHostedService, IDisposable
{
    /// <summary>Dispatched-event-id memory per consumer, bounding the catch-up/live dedupe.</summary>
    internal const int DedupeCapacity = 512;

    private readonly object _gate = new();
    private readonly Dictionary<string, (CancellationTokenSource Cts, Task Consumer)> _consumers = new(StringComparer.Ordinal);
    private CancellationTokenSource? _lifetime;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = new CancellationTokenSource();
        await RefreshAsync(cancellationToken);
        logger.LogInformation("EventTriggerEngine started with {Count} stream consumer(s).",
            _consumers.Count);
    }

    /// <summary>
    /// Aligns the stream consumers with the CURRENT enabled trigger set: opens a filtered
    /// consumer per new event type, stops consumers no trigger needs anymore.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (_lifetime is null)
            throw new InvalidOperationException("the engine is not started");

        var wanted = (await store.GetEventTriggersAsync(ct))
            .Where(t => t.Enabled)
            .Select(t => t.EventType)
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

    private async Task ConsumeAsync(string eventType, CancellationToken ct)
    {
        // The client stream reconnects internally; this loop only reacts to its frames. Track the
        // newest handled CreatedUtc so a reconnect can query the gap, and remember recently
        // dispatched event ids so catch-up overlapping the live stream dispatches once.
        DateTimeOffset? lastSeenUtc = null;
        var dispatched = new HashSet<Guid>();
        var dispatchedOrder = new Queue<Guid>();

        try
        {
            await foreach (var frame in core.StreamEventsAsync(eventType, ct: ct))
            {
                switch (frame)
                {
                    case StreamEventFrame<EventStreamEvent> evt:
                        if (Remember(evt.Event.Event.Id))
                        {
                            lastSeenUtc = Max(lastSeenUtc, evt.Event.Event.CreatedUtc);
                            await HandleAsync(evt.Event, ct);
                        }
                        break;

                    case StreamConnectionFrame<EventStreamEvent> { State: StreamConnectionState.Reconnecting } down:
                        logger.LogWarning(down.Cause,
                            "Event stream for '{EventType}' dropped — the client retries in {Backoff}s.",
                            eventType, down.RetryDelay?.TotalSeconds ?? 0);
                        break;

                    case StreamConnectionFrame<EventStreamEvent> { State: StreamConnectionState.Connected, Attempt: > 1 }:
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
                "Event stream consumer for '{EventType}' stopped on a non-recoverable error.",
                eventType);
        }
        return;

        bool Remember(Guid eventId)
        {
            if (!dispatched.Add(eventId))
                return false;
            dispatchedOrder.Enqueue(eventId);
            if (dispatchedOrder.Count > DedupeCapacity)
                dispatched.Remove(dispatchedOrder.Dequeue());
            return true;
        }

        static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x > b ? x : b;

        // Events persisted while disconnected: page the store oldest-first from the last
        // handled timestamp and run them through the same trigger dispatch (deduped above).
        async Task CatchUpAsync(CancellationToken innerCt)
        {
            if (lastSeenUtc is not { } since)
                return; // nothing handled yet — no gap to define
            try
            {
                while (true)
                {
                    var page = await core.QueryEventsAsync(
                        new EventQuery(eventType, CreatedAfterUtc: since, Take: 200), innerCt);
                    foreach (var evt in page.Items)
                    {
                        lastSeenUtc = Max(lastSeenUtc, evt.CreatedUtc);
                        if (Remember(evt.Id))
                            await HandleAsync(new EventStreamEvent(evt, evt.CreatedUtc), innerCt);
                    }
                    if (page.Items.Count == 0 || lastSeenUtc is not { } advanced || advanced <= since)
                        break;
                    since = advanced;
                }
                logger.LogInformation(
                    "Event stream for '{EventType}' reconnected — catch-up complete.", eventType);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Catch-up query for '{EventType}' failed — live events continue; the gap retries on the next reconnect.",
                    eventType);
            }
        }
    }

    /// <summary>
    /// Dispatches every enabled trigger matching the event (type + optional work-item filter).
    /// Triggers are read fresh from the store, so edits apply immediately. A failing dispatch
    /// is logged and does not stop the remaining triggers or the stream.
    /// </summary>
    public async Task HandleAsync(EventStreamEvent evt, CancellationToken ct = default)
    {
        var matching = (await store.GetEventTriggersAsync(ct))
            .Where(t => t.Enabled)
            .Where(t => string.Equals(t.EventType, evt.Event.EventType, StringComparison.Ordinal))
            .Where(t => t.WorkItemId is null
                        || string.Equals(t.WorkItemId, evt.Event.WorkItemId, StringComparison.Ordinal))
            .ToList();

        foreach (var trigger in matching)
        {
            var context = new Dictionary<string, string>
            {
                ["EventId"] = evt.Event.Id.ToString("D"),
                ["EventType"] = evt.Event.EventType,
                ["WorkItemId"] = evt.Event.WorkItemId
            };
            if (evt.Event.SourceRunId is { } sourceRun)
                context["SourceRunId"] = sourceRun.ToString("D");
            if (evt.Event.PayloadJson is { } payload)
                context["EventPayloadJson"] = payload;
            foreach (var (key, value) in trigger.Context ?? new Dictionary<string, string>())
                context.TryAdd(key, value);

            try
            {
                var accepted = await ScheduledTriggerEngine.DispatchAsync(
                    core, trigger.ConfigurationId, trigger.WorkflowType, context,
                    trigger.RunAsPrincipalId, ct);
                logger.LogInformation(
                    "Event trigger {TriggerId} dispatched. Event={EventType} RunId={RunId}",
                    trigger.Id, evt.Event.EventType, accepted.RunId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex,
                    "Event trigger {TriggerId} failed to dispatch for event {EventId}.",
                    trigger.Id, evt.Event.Id);
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
