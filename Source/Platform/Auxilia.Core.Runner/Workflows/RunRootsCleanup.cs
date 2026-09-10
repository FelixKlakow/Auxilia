namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Best-effort removal of a run's on-host roots: repository workspace, pod resources, the
/// per-run output directory, and the extracted ZIP package. Shared by every terminal path —
/// the graceful one (<see cref="WorkflowStateHandler"/>, after artifact persistence), the
/// pre-flight rejection (<see cref="WorkflowDispatcher"/> — the roots may already be prepared
/// when a later check fails), the crash path
/// (<see cref="WorkflowDispatcher.HandleContainerExitAsync"/> — a crashed workflow never sends
/// the terminal state message) and the re-adoption sweeps (<see cref="WorkflowReadoptionService"/>).
/// Idempotent; every failure is logged, never thrown.
/// </summary>
internal static class RunRootsCleanup
{
    /// <summary>
    /// Extraction directory of a ZIP-packaged run — deterministic per instance so every cleanup
    /// path can find it without shared state (it is bind-mounted read-only at <c>/workflow</c>).
    /// </summary>
    internal static string PackageDirectoryFor(Guid instanceId)
        => Path.Combine(Path.GetTempPath(), $"auxilia-wf-{instanceId:N}");

    /// <summary>Per-run output directory (mounted at <c>/workflow-output</c>).</summary>
    internal static string OutputDirectoryFor(WorkflowDispatcherSettings settings, Guid instanceId)
        => Path.Combine(settings.RunOutputDirectory, instanceId.ToString("N"));

    /// <summary>
    /// Sweeps the run's roots and returns the torn-down companions' log tails (empty for a
    /// pod-less run). <paramref name="includePackage"/> is false only while the container may
    /// still be alive (the graceful terminal message precedes the exit) — the exit watcher
    /// deletes the package once the read-only bind is released.
    /// </summary>
    public static async Task<IReadOnlyList<Pods.CompanionLog>> CleanupAsync(
        WorkspaceManager workspaceManager,
        Pods.IPodHost podHost,
        WorkflowDispatcherSettings settings,
        ILogger logger,
        Guid instanceId,
        bool includePackage = true,
        CancellationToken ct = default)
    {
        await workspaceManager.CleanupAsync(instanceId);
        var companionLogs = await podHost.TeardownAsync(instanceId, ct);
        DeleteDirectory(OutputDirectoryFor(settings, instanceId), "run output root", logger);
        if (includePackage)
            DeletePackageDirectory(logger, instanceId);
        return companionLogs;
    }

    /// <summary>Deletes the run's extracted package (no-op for baked-image runs).</summary>
    public static void DeletePackageDirectory(ILogger logger, Guid instanceId)
        => DeleteDirectory(PackageDirectoryFor(instanceId), "extracted package", logger);

    private static void DeleteDirectory(string directory, string description, ILogger logger)
    {
        try
        {
            if (!Directory.Exists(directory))
                return;
            // Git (a workflow may clone into its output) marks object files read-only.
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the {Description} {Directory}.", description, directory);
        }
    }
}
