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
    CoreRunnerInfo runnerInfo,
    AuditLog auditLog,
    WorkflowStatusPublisher statusPublisher,
    ArtifactPersister artifactPersister,
    WorkspaceManager workspaceManager,
    Pods.IPodHost podHost,
    Pods.PodControlRegistry podControlRegistry,
    IOptions<WorkflowDispatcherSettings> dispatcherSettings,
    Auxilia.PlatformData.Protection.ISettingsProtector settingsProtector,
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
        // Every container holds the bus password: only the instance itself (its token, bound to
        // its type) may report its terminal state — otherwise any run could end any other run.
        if (dispatcherSettings.Value.RequireInstanceToken &&
            !tokenRegistry.Validate(message.WorkflowInstanceId, message.InstanceToken, message.WorkflowName))
        {
            logger.LogWarning(
                "Rejected WorkflowStateMessage with missing or invalid instance token. InstanceId={InstanceId} Workflow={WorkflowName} State={State}",
                message.WorkflowInstanceId, message.WorkflowName, message.State);
            await auditLog.AppendAsync(
                "steering-instance", "workflow.state.rejected",
                message.WorkflowInstanceId.ToString(), "invalid-instance-token", ct: ct);
            return;
        }

        // The state exchange fans out to every runner; only the owner acts. A foreign or
        // unknown instance is not ours to terminate, publish, or clean up.
        var record = await instanceRegistry.GetAsync(message.WorkflowInstanceId, ct);
        if (record is null || record.OwnerServiceId != runnerInfo.ServiceId)
        {
            logger.LogDebug(
                "Ignoring WorkflowStateMessage for instance {InstanceId} — not owned by this runner.",
                message.WorkflowInstanceId);
            return;
        }

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

        // The instance credential dies with the run — no further slot activations.
        tokenRegistry.Consume(message.WorkflowInstanceId);

        await instanceRegistry.SetStateAsync(
            message.WorkflowInstanceId, message.State.ToString(), message.ErrorMessage, ct);
        await statusPublisher.PublishAsync(
            message.WorkflowInstanceId, record.WorkflowType,
            message.State.ToString(), message.ErrorMessage,
            ownerServiceId: record.OwnerServiceId, ct: ct);
        await auditLog.AppendAsync(
            "steering-instance", "workflow.state-changed",
            message.WorkflowInstanceId.ToString(), message.State.ToString(),
            message.ErrorMessage is null ? null : $$"""{"error":{{JsonSerializer.Serialize(message.ErrorMessage)}}}""",
            ct);

        var workItemId = string.Empty;
        if (record.DispatchCommandJson is not null)
        {
            var command = JsonSerializer.Deserialize<RunWorkflowCommand>(record.DispatchCommandJson);
            command?.Context.TryGetValue("WorkItemId", out workItemId!);
        }

        // Declared outputs of a successful run are persisted to the artifact store.
        if (message.State == WorkflowState.Success)
            await artifactPersister.PersistOutputsAsync(
                message.WorkflowInstanceId, record.WorkflowType, record.OutputsJson,
                workItemId ?? string.Empty, ct);

        // The run's repository workspace, pod AND output directory die with the run on every
        // terminal state (ARCHITECTURE §9, run-pod design §A) — after the declared outputs were
        // persisted; every companion's log tail becomes a post-mortem artifact. The extracted
        // package is NOT swept here: the container may still be exiting, and the exit watcher
        // deletes it once the bind is released.
        if (message.State is WorkflowState.Success or WorkflowState.Failed or WorkflowState.Cancelled)
        {
            podControlRegistry.Consume(message.WorkflowInstanceId);
            var companionLogs = await RunRootsCleanup.CleanupAsync(
                workspaceManager, podHost, dispatcherSettings.Value, logger,
                message.WorkflowInstanceId, includePackage: false, ct);
            if (companionLogs.Count > 0)
                await artifactPersister.PersistCompanionLogsAsync(
                    message.WorkflowInstanceId, record.WorkflowType,
                    companionLogs, workItemId ?? string.Empty, ct);
        }

        // Drain-and-replace: a drained long-living instance is replaced with a fresh run
        // that boots with the updated configuration (ARCHITECTURE §6). Shared with the
        // dispatcher's drain-crash path — a container dying mid-drain never reaches here.
        if (record is { State: "Draining" })
            await DrainReplacement.PublishAsync(
                messageBus, settingsProtector, auditLog,
                dispatcherSettings.Value.CommandQueueName,
                message.WorkflowInstanceId, record.DispatchCommandJson, logger, ct);
    }

    public async ValueTask StopAsync()
    {
        if (_subscription is not null)
            await _subscription.DisposeAsync();
    }
}
