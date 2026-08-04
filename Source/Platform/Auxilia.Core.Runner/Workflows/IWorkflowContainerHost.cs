namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// The container-lifecycle surface the re-adoption pass needs beyond launching: list this host's
/// workflow containers, re-attach exit watchers, and clean-kill what cannot be adopted.
/// Implemented by <see cref="DockerWorkflowLauncher"/>; tests inject a fake.
/// </summary>
public interface IWorkflowContainerHost
{
    /// <summary>All workflow containers on this host (running AND exited).</summary>
    Task<IReadOnlyList<WorkflowContainerInfo>> ListWorkflowContainersAsync(CancellationToken ct = default);

    /// <summary>
    /// Re-attaches the exit watcher: waits for the container's exit (immediate for an already
    /// exited one), collects exit code + log tail, removes the container, reports via
    /// <paramref name="onExited"/>.
    /// </summary>
    void AttachExitWatcher(string containerId, Func<ContainerExit, Task> onExited);

    /// <summary>Force-removes a container that cannot be re-adopted.</summary>
    Task RemoveContainerAsync(string containerId, CancellationToken ct = default);
}

/// <summary>One workflow container as seen at startup: id, run mapping (label), liveness.</summary>
public sealed record WorkflowContainerInfo(string ContainerId, Guid? InstanceId, bool IsRunning);
