namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Best-effort removal of a run's on-host roots: repository workspace, pod resources, and the
/// per-run output directory. Shared by the crash path
/// (<see cref="WorkflowDispatcher.HandleContainerExitAsync"/> — a crashed workflow never sends
/// the terminal state message that normally triggers cleanup) and the re-adoption sweeps
/// (<see cref="WorkflowReadoptionService"/>). Idempotent; every failure is logged, never thrown.
/// </summary>
internal static class RunRootsCleanup
{
    public static async Task CleanupAsync(
        WorkspaceManager workspaceManager,
        Pods.IPodHost podHost,
        WorkflowDispatcherSettings settings,
        ILogger logger,
        Guid instanceId)
    {
        await workspaceManager.CleanupAsync(instanceId);
        await podHost.TeardownAsync(instanceId);
        var outputRoot = Path.Combine(settings.RunOutputDirectory, instanceId.ToString("N"));
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
}
