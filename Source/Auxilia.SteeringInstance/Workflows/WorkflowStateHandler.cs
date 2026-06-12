using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class WorkflowStateHandler(
    IMessageBusClient messageBus,
    WorkflowInstanceRegistry instanceRegistry,
    AuditLog auditLog,
    ILogger<WorkflowStateHandler> logger)
{
    private IAsyncDisposable? _subscription;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await messageBus.DeclareExchangeAsync("workflow.state", ct);
        _subscription = await messageBus.SubscribeToExchangeAsync<WorkflowStateMessage>(
            "workflow.state", HandleAsync, ct);

        logger.LogInformation("WorkflowStateHandler started — listening on workflow.state.");
    }

    private async Task HandleAsync(WorkflowStateMessage message, CancellationToken ct)
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

        await instanceRegistry.SetStateAsync(
            message.WorkflowInstanceId, message.State.ToString(), message.ErrorMessage, ct);
        await auditLog.AppendAsync(
            "steering-instance", "workflow.state-changed",
            message.WorkflowInstanceId.ToString(), message.State.ToString(),
            message.ErrorMessage is null ? null : $$"""{"error":{{System.Text.Json.JsonSerializer.Serialize(message.ErrorMessage)}}}""",
            ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
