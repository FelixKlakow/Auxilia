using System.Text.Json;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Workspace;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Startup re-adoption: a restarted runner RECONNECTS to the workflow containers its previous
/// process left behind instead of killing them. Matched running containers get their exit
/// watchers re-attached and their instance tokens restored (the in-container SDK keeps
/// authenticating); containers that exited during the downtime get their REAL exit collected;
/// records without a container are failed visibly; containers without a live record are
/// clean-killed with their run roots removed. Runs FIRST at startup — the re-claim (new
/// OwnerServiceId published per run) must land before the Core's failover clock (~heartbeat
/// timeout since the old process died) expires. If the downtime was longer, the Core has
/// already failed the run over: the re-claim is dropped by its terminal sink and the queued
/// cancel stops the container — accepted, documented behavior.
/// </summary>
public sealed class WorkflowReadoptionService(
    IWorkflowContainerHost containerHost,
    WorkflowInstanceRegistry instanceRegistry,
    WorkflowInstanceTokenRegistry tokenRegistry,
    WorkflowSchemaStore schemaStore,
    Pods.PodControlRegistry podControlRegistry,
    WorkflowDispatcher dispatcher,
    WorkflowStatusPublisher statusPublisher,
    WorkspaceManager workspaceManager,
    ISettingsProtector settingsProtector,
    IOptions<WorkflowDispatcherSettings> dispatcherSettings,
    CoreRunnerInfo instanceInfo,
    AuditLog auditLog,
    Pods.IPodHost podHost,
    ILogger<WorkflowReadoptionService> logger)
{
    public async Task<ReadoptionPlan> RunAsync(CancellationToken ct = default)
    {
        var containers = await containerHost.ListWorkflowContainersAsync(ct);
        var records = await instanceRegistry.GetNonTerminalAsync(ct);
        var plan = ReadoptionPlanner.Plan(records, containers);

        // RE-CLAIM FIRST, in bulk, before any per-container Docker work: stamping the fresh
        // ServiceId (preserve-last-non-null owner contract) and touching UpdatedUtc resets the
        // Core's failover clock for every run that still has a container.
        foreach (var (record, _) in plan.Adopt.Concat(plan.CollectExit))
        {
            await instanceRegistry.SetOwnerAsync(record.Id, instanceInfo.ServiceId, ct);
            await statusPublisher.PublishAsync(
                record.Id, record.WorkflowType, record.State,
                ownerServiceId: instanceInfo.ServiceId,
                commandId: CommandIdOf(record),
                terminalEndpoint: record.TerminalEndpoint, ct: ct);
        }

        foreach (var (record, container) in plan.Adopt)
        {
            RestoreToken(record);
            await RestorePodControlAsync(record, ct);
            containerHost.AttachExitWatcher(container.ContainerId,
                exit => HandleReadoptedExitAsync(record, exit));
            logger.LogInformation(
                "Re-adopted running workflow container. InstanceId={InstanceId} ContainerId={ContainerId} State={State}",
                record.Id, Short(container.ContainerId), record.State);
            await auditLog.AppendAsync("core-runner", "workflow.readopted",
                record.Id.ToString(), Short(container.ContainerId), ct: ct);
        }

        foreach (var (record, container) in plan.CollectExit)
        {
            // The wait returns immediately for an exited container: its real exit code + log
            // tail flow through the normal container-exit path (grace, Failed, token consume).
            containerHost.AttachExitWatcher(container.ContainerId,
                exit => HandleReadoptedExitAsync(record, exit));
            logger.LogWarning(
                "Workflow container exited while the runner was down — collecting its exit. InstanceId={InstanceId} ContainerId={ContainerId}",
                record.Id, Short(container.ContainerId));
        }

        foreach (var record in plan.FailRecord)
        {
            const string reason = "runner restarted and the run's container no longer exists";
            logger.LogWarning(
                "Non-terminal run {InstanceId} ({State}) has no container after restart — failing it.",
                record.Id, record.State);
            await instanceRegistry.SetStateAsync(record.Id, "Failed", reason, ct);
            await statusPublisher.PublishAsync(
                record.Id, record.WorkflowType, "Failed", reason,
                ownerServiceId: instanceInfo.ServiceId, commandId: CommandIdOf(record), ct: ct);
            tokenRegistry.Consume(record.Id);
            await CleanupRunRootsAsync(record.Id);
            await auditLog.AppendAsync("core-runner", "workflow.readoption.container-missing",
                record.Id.ToString(), reason, ct: ct);
        }

        foreach (var kill in plan.CleanKill)
        {
            logger.LogWarning(
                "Removing workflow container without a live run. ContainerId={ContainerId} InstanceId={InstanceId}",
                Short(kill.ContainerId), kill.InstanceId);
            await containerHost.RemoveContainerAsync(kill.ContainerId, ct);
            if (kill.InstanceId is { } instanceId)
                await CleanupRunRootsAsync(instanceId);
        }

        // Pods whose run no longer lives here die with their runs: everything except the
        // adopted (and exit-collecting, torn down via their exit path) instances is swept.
        var live = plan.Adopt.Concat(plan.CollectExit).Select(p => p.Record.Id).ToHashSet();
        await podHost.SweepOrphanedAsync(live, ct);

        logger.LogInformation(
            "Re-adoption complete. Adopted={Adopted} ExitsCollected={Exits} FailedRecords={Failed} CleanKilled={Killed}",
            plan.Adopt.Count, plan.CollectExit.Count, plan.FailRecord.Count, plan.CleanKill.Count);
        return plan;
    }

    private void RestoreToken(WorkflowInstanceRecord record)
    {
        if (record.ProtectedInstanceToken is not { Length: > 0 } protectedToken)
        {
            logger.LogWarning(
                "Run {InstanceId} has no persisted instance token — its SDK cannot authenticate after the restart.",
                record.Id);
            return;
        }
        try
        {
            tokenRegistry.Restore(
                record.Id, settingsProtector.Unprotect(protectedToken), record.WorkflowType,
                registered: record.State is "Running" or "Draining");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A protection-key change makes old tokens unrecoverable; the run keeps executing,
            // only its future JIT slot activations fail — visibly, at the credential endpoint.
            logger.LogWarning(ex, "Restoring the instance token of {InstanceId} failed.", record.Id);
        }
    }

    /// <summary>
    /// Rebuilds the run's pod-control state after a restart — the registry is runner-memory.
    /// Everything is reconstructable: the envelope from the schema store, the base map from
    /// the persisted dispatch command, network/volume names deterministically re-planned by
    /// <see cref="Pods.PodPlanner"/> — so an adopted run keeps its runtime spawn capability.
    /// </summary>
    private async Task RestorePodControlAsync(WorkflowInstanceRecord record, CancellationToken ct)
    {
        var schema = await schemaStore.GetSchemaAsync(record.WorkflowType, ct);
        if (schema?.PodControl is not { } podControl)
            return;
        if (DispatchCommandOf(record) is not { } command)
        {
            logger.LogWarning(
                "Run {InstanceId} declares a pod-control envelope but has no persisted dispatch command — runtime spawns stay refused.",
                record.Id);
            return;
        }
        var podPlan = Pods.PodPlanner.Plan(
            schema.Companions, podControl, command.Context, record.Id);
        if (podPlan is null)
            return;
        podControlRegistry.Register(record.Id, new Pods.PodControlState(
            podControl.MaxContainers,
            WorkflowDispatcher.ParseBaseImages(command.PodBaseImagesJson),
            podPlan.NetworkName,
            podPlan.Volumes.ToDictionary(v => v.Name, v => v.DockerVolumeName)));
        await auditLog.AppendAsync("core-runner", "workflow.pod-control.restored",
            record.Id.ToString(), podControl.MaxContainers.ToString(), ct: ct);
    }

    private async Task HandleReadoptedExitAsync(WorkflowInstanceRecord record, ContainerExit exit)
    {
        await dispatcher.HandleContainerExitAsync(record.Id, record.WorkflowType, exit);
        // The runner-side workspace/output cleanup normally rides the workflow.state terminal
        // event; a re-adopted container may die without one — sweep here (idempotent).
        await CleanupRunRootsAsync(record.Id);
    }

    private async Task CleanupRunRootsAsync(Guid instanceId)
    {
        await workspaceManager.CleanupAsync(instanceId);
        await podHost.TeardownAsync(instanceId);
        var outputRoot = Path.Combine(
            dispatcherSettings.Value.RunOutputDirectory, instanceId.ToString("N"));
        try
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete run output root {OutputRoot}.", outputRoot);
        }
    }

    private static Guid? CommandIdOf(WorkflowInstanceRecord record)
        => DispatchCommandOf(record)?.CommandId;

    private static RunWorkflowCommand? DispatchCommandOf(WorkflowInstanceRecord record)
    {
        if (record.DispatchCommandJson is not { Length: > 0 } json)
            return null;
        try
        {
            return JsonSerializer.Deserialize<RunWorkflowCommand>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Short(string containerId) => containerId[..Math.Min(12, containerId.Length)];
}

/// <summary>The four re-adoption outcomes, decided purely from records × containers.</summary>
public sealed record ReadoptionPlan(
    IReadOnlyList<(WorkflowInstanceRecord Record, WorkflowContainerInfo Container)> Adopt,
    IReadOnlyList<(WorkflowInstanceRecord Record, WorkflowContainerInfo Container)> CollectExit,
    IReadOnlyList<WorkflowInstanceRecord> FailRecord,
    IReadOnlyList<CleanKillTarget> CleanKill);

/// <summary>A container to remove; the label-resolved instance id locates its run roots.</summary>
public sealed record CleanKillTarget(string ContainerId, Guid? InstanceId);

/// <summary>
/// Pure matching logic (unit-testable without Docker): non-terminal records match containers by
/// persisted container id, falling back to the container's instance-id label. Running matches
/// are adopted; exited matches get their exit collected; unmatched records fail; unmatched or
/// terminal-owned containers are clean-killed.
/// </summary>
public static class ReadoptionPlanner
{
    public static ReadoptionPlan Plan(
        IReadOnlyList<WorkflowInstanceRecord> nonTerminalRecords,
        IReadOnlyList<WorkflowContainerInfo> containers)
    {
        var adopt = new List<(WorkflowInstanceRecord, WorkflowContainerInfo)>();
        var collectExit = new List<(WorkflowInstanceRecord, WorkflowContainerInfo)>();
        var failRecord = new List<WorkflowInstanceRecord>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in nonTerminalRecords)
        {
            var container = containers.FirstOrDefault(c =>
                                !claimed.Contains(c.ContainerId)
                                && record.ContainerId is { Length: > 0 } id
                                && string.Equals(c.ContainerId, id, StringComparison.Ordinal))
                            ?? containers.FirstOrDefault(c =>
                                !claimed.Contains(c.ContainerId) && c.InstanceId == record.Id);
            if (container is null)
            {
                failRecord.Add(record);
                continue;
            }
            claimed.Add(container.ContainerId);
            if (container.IsRunning)
                adopt.Add((record, container));
            else
                collectExit.Add((record, container));
        }

        var cleanKill = containers
            .Where(c => !claimed.Contains(c.ContainerId))
            .Select(c => new CleanKillTarget(c.ContainerId, c.InstanceId))
            .ToList();

        return new ReadoptionPlan(adopt, collectExit, failRecord, cleanKill);
    }
}
