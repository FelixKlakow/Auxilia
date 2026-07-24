using System.Text.Json;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

public sealed class WorkflowStateHandler(
    IMessageBusClient messageBus,
    WorkflowInstanceRegistry instanceRegistry,
    Storage.WorkflowInstanceTokenRegistry tokenRegistry,
    AuditLog auditLog,
    WorkflowStatusPublisher statusPublisher,
    ArtifactPersister artifactPersister,
    WorkspaceManager workspaceManager,
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

        // The instance credential dies with the run — no further slot activations.
        tokenRegistry.Consume(message.WorkflowInstanceId);

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

        // Declared outputs of a successful run are persisted to the artifact store.
        if (message.State == WorkflowState.Success && record is not null)
        {
            var workItemId = string.Empty;
            if (record.DispatchCommandJson is not null)
            {
                var command = JsonSerializer.Deserialize<RunWorkflowCommand>(record.DispatchCommandJson);
                command?.Context.TryGetValue("WorkItemId", out workItemId!);
            }

            await artifactPersister.PersistOutputsAsync(
                message.WorkflowInstanceId, record.WorkflowType, record.OutputsJson,
                workItemId ?? string.Empty, ct);
        }

        // The run's repository workspace dies with the run (ARCHITECTURE §9).
        if (message.State is WorkflowState.Success or WorkflowState.Failed or WorkflowState.Cancelled)
            await workspaceManager.CleanupAsync(message.WorkflowInstanceId);

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
