using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class WorkflowCancelDispatcher(
    IMessageBusClient messageBus,
    WorkflowInstanceRegistry instanceRegistry,
    ILogger<WorkflowCancelDispatcher> logger)
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await messageBus.DeclareQueueAsync("workflow.cancel-commands", ct);
        _subscription = await messageBus.SubscribeAsync<CancelWorkflowCommand>(
            "workflow.cancel-commands", HandleAsync, ct);

        logger.LogInformation("WorkflowCancelDispatcher started — listening on workflow.cancel-commands.");
    }

    private async Task HandleAsync(CancelWorkflowCommand command, CancellationToken ct)
    {
        var typeName = await instanceRegistry.GetWorkflowTypeAsync(command.WorkflowInstanceId, ct);
        if (typeName is null)
        {
            logger.LogWarning(
                "Received CancelWorkflowCommand for unknown instance {InstanceId} — ignoring.",
                command.WorkflowInstanceId);
            return;
        }

        var topic = $"workflow-cancel-{command.WorkflowInstanceId}";
        await messageBus.PublishAsync(topic, command, ct);

        logger.LogInformation(
            "Dispatched cancel command to {Topic} for instance {InstanceId} (type: {WorkflowType}).",
            topic, command.WorkflowInstanceId, typeName);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
