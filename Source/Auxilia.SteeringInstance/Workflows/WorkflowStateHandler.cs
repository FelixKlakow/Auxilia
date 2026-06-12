using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Workflows;

public sealed class WorkflowStateHandler(
    IMessageBusClient messageBus,
    WorkflowInstanceRegistry instanceRegistry,
    AuditLog auditLog,
    WorkflowStatusPublisher statusPublisher,
    IOptions<WorkflowDispatcherSettings> dispatcherSettings,
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

        var record = await instanceRegistry.GetAsync(message.WorkflowInstanceId, ct);

        await instanceRegistry.SetStateAsync(
            message.WorkflowInstanceId, message.State.ToString(), message.ErrorMessage, ct);
        await statusPublisher.PublishAsync(
            message.WorkflowInstanceId, record?.WorkflowType ?? "unknown",
            message.State.ToString(), message.ErrorMessage, ct);
        await auditLog.AppendAsync(
            "steering-instance", "workflow.state-changed",
            message.WorkflowInstanceId.ToString(), message.State.ToString(),
            message.ErrorMessage is null ? null : $$"""{"error":{{JsonSerializer.Serialize(message.ErrorMessage)}}}""",
            ct);

        // Drain-and-replace: a drained long-living instance is replaced with a fresh run
        // that boots with the updated configuration (ARCHITECTURE §6).
        if (record is { State: "Draining", DispatchCommandJson: not null })
        {
            var original = JsonSerializer.Deserialize<RunWorkflowCommand>(record.DispatchCommandJson);
            if (original is not null)
            {
                var replacement = original with { CommandId = Guid.NewGuid() };
                await messageBus.PublishAsync(
                    dispatcherSettings.Value.CommandQueueName, replacement, ct);
                await auditLog.AppendAsync(
                    "steering-instance", "workflow.drain-replaced",
                    message.WorkflowInstanceId.ToString(), replacement.CommandId.ToString(), ct: ct);
                logger.LogInformation(
                    "Drained instance {InstanceId} replaced — new dispatch {CommandId}.",
                    message.WorkflowInstanceId, replacement.CommandId);
            }
        }
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
