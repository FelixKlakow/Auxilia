using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Bridges the bus event feed to the SSE <see cref="EventStreamBroker"/> with SELECTIVE
/// ingest: subscribes to the <c>workflow.events</c> topic exchange with no initial bindings
/// and, as <see cref="IEventStreamBindingListener"/>, binds/unbinds the event-type routing
/// keys this node's open streams filter on (an unfiltered stream binds the match-all key).
/// This is what lets event-trigger clients react to events WITHOUT a bus subscription.
/// </summary>
public sealed class EventStreamPublisher(
    IMessageBusClient bus,
    EventStreamBroker broker,
    ILogger<EventStreamPublisher> logger) : IHostedService, IEventStreamBindingListener
{
    private const string MatchAll = "#";

    private ITopicSubscription? _subscription;

    // Serializes all binding mutations and guards the audience refcounts (key = binding key).
    private readonly SemaphoreSlim _bindGate = new(1, 1);
    private readonly Dictionary<string, int> _audience = new(StringComparer.Ordinal);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await bus.DeclareTopicExchangeAsync(WorkflowEventMessage.ExchangeName, cancellationToken);
        _subscription = await bus.SubscribeToTopicExchangeAsync<WorkflowEventMessage>(
            WorkflowEventMessage.ExchangeName, [], HandleAsync, cancellationToken);
        broker.SetListener(this);
        logger.LogInformation("EventStreamPublisher listening selectively on {Exchange}.",
            WorkflowEventMessage.ExchangeName);
    }

    public async Task EventInterestAddedAsync(string? eventType, CancellationToken ct)
    {
        var key = BindingKeyFor(eventType);
        await _bindGate.WaitAsync(ct);
        try
        {
            var count = _audience.GetValueOrDefault(key) + 1;
            _audience[key] = count;
            if (count == 1)
                await _subscription!.AddBindingAsync(key, ct);
        }
        finally { _bindGate.Release(); }
    }

    public async Task EventInterestRemovedAsync(string? eventType)
    {
        var key = BindingKeyFor(eventType);
        try
        {
            await _bindGate.WaitAsync();
            try
            {
                var count = _audience.GetValueOrDefault(key) - 1;
                if (count > 0)
                {
                    _audience[key] = count;
                    return;
                }
                _audience.Remove(key);
                await _subscription!.RemoveBindingAsync(key);
            }
            finally { _bindGate.Release(); }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove the '{Key}' event binding.", key);
        }
    }

    private static string BindingKeyFor(string? eventType)
        => eventType is null ? MatchAll : WorkflowEventMessage.RoutingKeyFor(eventType);

    private Task HandleAsync(WorkflowEventMessage evt, CancellationToken ct)
    {
        // Same guard as the tracking mirror: spoofed run.* events never reach a subscriber.
        if (!RunLifecycleEventPublisher.IsAuthentic(evt))
            return Task.CompletedTask;
        broker.Publish(new EventStreamEvent(
            new EventDto(
                evt.EventId, evt.EventType, evt.WorkflowType, evt.WorkItemId,
                evt.WorkflowInstanceId == Guid.Empty ? null : evt.WorkflowInstanceId,
                evt.PayloadJson, evt.TimestampUtc),
            evt.TimestampUtc));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
