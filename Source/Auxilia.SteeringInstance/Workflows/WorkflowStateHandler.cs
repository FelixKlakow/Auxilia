using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class WorkflowStateHandler(
    IMessageBusClient messageBus,
    ILogger<WorkflowStateHandler> logger)
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await messageBus.DeclareQueueAsync("workflow.state", ct);
        _subscription = await messageBus.SubscribeAsync<WorkflowStateMessage>(
            "workflow.state", HandleAsync, ct);

        logger.LogInformation("WorkflowStateHandler started — listening on workflow.state.");
    }

    private Task HandleAsync(WorkflowStateMessage message, CancellationToken ct)
    {
        switch (message.State)
        {
            case WorkflowState.Success:
                logger.LogInformation(
                    "Workflow {InstanceId} completed successfully.",
                    message.WorkflowInstanceId);
                break;
            case WorkflowState.Failed:
                logger.LogWarning(
                    "Workflow {InstanceId} failed: {ErrorMessage}",
                    message.WorkflowInstanceId, message.ErrorMessage);
                break;
            case WorkflowState.Cancelled:
                logger.LogInformation(
                    "Workflow {InstanceId} was cancelled.",
                    message.WorkflowInstanceId);
                break;
            default:
                logger.LogWarning(
                    "Workflow {InstanceId} reported unknown state {State}.",
                    message.WorkflowInstanceId, message.State);
                break;
        }

        return Task.CompletedTask;
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
