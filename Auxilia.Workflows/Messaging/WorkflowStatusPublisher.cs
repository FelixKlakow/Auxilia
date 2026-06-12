using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Messaging;

/// <summary>
/// Publishes lifecycle transitions to the <see cref="WorkflowStatusEvent.ExchangeName"/> fanout
/// exchange. Every transition goes through here so failures and restarts are never silent.
/// </summary>
public sealed class WorkflowStatusPublisher(IMessageBusClient messageBus, TimeProvider timeProvider)
{
    private bool _exchangeDeclared;

    public async Task PublishAsync(
        Guid instanceId, string workflowType, string state, string? errorMessage = null,
        CancellationToken ct = default)
    {
        if (!_exchangeDeclared)
        {
            await messageBus.DeclareExchangeAsync(WorkflowStatusEvent.ExchangeName, ct);
            _exchangeDeclared = true;
        }

        await messageBus.PublishToExchangeAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, workflowType, state, errorMessage, timeProvider.GetUtcNow()), ct);
    }
}
