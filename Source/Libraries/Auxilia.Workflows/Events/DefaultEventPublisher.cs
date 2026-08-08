using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Events;

/// <summary>
/// Bus-backed event publisher: validates the event type is declared and not platform-reserved,
/// caps the payload (large data belongs in the Artifact Store, referenced from the payload),
/// and publishes to the <see cref="WorkflowEventMessage.ExchangeName"/> topic exchange keyed
/// by event type.
/// </summary>
public sealed class DefaultEventPublisher(
    IMessageBusClient messageBus,
    Guid instanceId,
    string workflowType,
    IReadOnlyList<EventDescriptor> declaredEvents) : IEventPublisher
{
    internal const int MaxPayloadBytes = 64 * 1024;

    private bool _exchangeDeclared;

    public Task PublishAsync<TPayload>(
        string eventType, TPayload payload, string? workItemId = null, CancellationToken ct = default)
        => PublishCoreAsync(eventType, JsonSerializer.Serialize(payload), workItemId, ct);

    public Task PublishAsync(string eventType, string? workItemId = null, CancellationToken ct = default)
        => PublishCoreAsync(eventType, payloadJson: null, workItemId, ct);

    private async Task PublishCoreAsync(
        string eventType, string? payloadJson, string? workItemId, CancellationToken ct)
    {
        if (eventType.StartsWith(WorkflowEventMessage.ReservedPlatformPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Event type '{eventType}' uses the reserved platform prefix " +
                $"'{WorkflowEventMessage.ReservedPlatformPrefix}'.");
        if (!declaredEvents.Any(e => e.EventType == eventType))
            throw new InvalidOperationException(
                $"Event '{eventType}' is not declared by this workflow. Declare it via DeclaresEvent.");
        if (payloadJson is not null && System.Text.Encoding.UTF8.GetByteCount(payloadJson) > MaxPayloadBytes)
            throw new InvalidOperationException(
                $"Event '{eventType}' payload exceeds {MaxPayloadBytes / 1024} KB — persist the data " +
                "as an artifact and reference it from the payload.");

        if (!_exchangeDeclared)
        {
            await messageBus.DeclareTopicExchangeAsync(WorkflowEventMessage.ExchangeName, ct);
            _exchangeDeclared = true;
        }

        await messageBus.PublishToTopicExchangeAsync(WorkflowEventMessage.ExchangeName,
            WorkflowEventMessage.RoutingKeyFor(eventType),
            new WorkflowEventMessage(Guid.NewGuid(), eventType, instanceId, workflowType,
                workItemId ?? string.Empty, payloadJson, DateTimeOffset.UtcNow), ct);
    }
}
