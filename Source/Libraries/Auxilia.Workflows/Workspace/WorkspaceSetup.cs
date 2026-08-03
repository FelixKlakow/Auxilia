using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Auxilia.Workflows.Workspace;

/// <summary>
/// Post-binding workspace setup (ARCHITECTURE §9): repository setup scripts run INSIDE the
/// workflow container after the workspace is bound and before the application — under the run's
/// egress policy. Two sources: the workflow's own manifest declarations (signed with the
/// package) and mount-bound scripts announced via
/// <see cref="WorkflowEnvironmentVariables.WorkspaceMountSetupPrefix"/>. Fail-fast: a non-zero
/// exit fails the run before the application ever starts.
/// </summary>
public static class WorkspaceSetup
{
    internal sealed record SetupTask(string MountId, string Root, string Script);

    public static async Task RunDeclaredAsync(
        IReadOnlyList<RepositoryDeclaration> declared, ILogger logger, CancellationToken cancellationToken)
    {
        var environment = SnapshotEnvironment();
        var tasks = Collect(
            declared,
            environment.GetValueOrDefault(WorkflowEnvironmentVariables.WorkspaceDirectory),
            environment);
        foreach (var task in tasks)
        {
            logger.LogInformation(
                "Running workspace setup for '{MountId}' in '{Root}'.", task.MountId, task.Root);
            await RunScriptAsync(task, logger, cancellationToken);
        }
    }

    /// <summary>
    /// Manifest scripts resolve against the prepared per-run copy under the workspace root (and
    /// are skipped entirely when no workspace was prepared — the dev/test-harness case); a
    /// mount-announced script without its root variable is a wiring error and fails the run.
    /// </summary>
    internal static IReadOnlyList<SetupTask> Collect(
        IReadOnlyList<RepositoryDeclaration> declared,
        string? workspaceDirectory,
        IReadOnlyDictionary<string, string> environment)
    {
        var tasks = new List<SetupTask>();
        if (workspaceDirectory is { Length: > 0 })
            foreach (var repository in declared)
                if (repository.SetupScript is { Length: > 0 } script)
                    tasks.Add(new SetupTask(
                        repository.Id,
                        Path.Combine(workspaceDirectory, "repos", repository.Id),
                        script));

        foreach (var (key, script) in environment)
        {
            if (!key.StartsWith(WorkflowEnvironmentVariables.WorkspaceMountSetupPrefix, StringComparison.Ordinal)
                || script.Length == 0)
                continue;
            var suffix = key[WorkflowEnvironmentVariables.WorkspaceMountSetupPrefix.Length..];
            var root = environment.GetValueOrDefault(WorkflowEnvironmentVariables.WorkspaceMountPrefix + suffix)
                       ?? throw new InvalidOperationException(
                           $"Workspace setup for mount '{suffix}' was announced without a mount root.");
            tasks.Add(new SetupTask(suffix, root, script));
        }

        return tasks;
    }

    internal static async Task RunScriptAsync(SetupTask task, ILogger logger, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(task.Root))
            throw new InvalidOperationException(
                $"Workspace setup for mount '{task.MountId}' has no prepared directory at '{task.Root}'.");

        // Containers are Linux (sh); the Windows branch exists for local dev/test runs only.
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo { FileName = "cmd.exe", ArgumentList = { "/d", "/c", task.Script } }
            : new ProcessStartInfo { FileName = "/bin/sh", ArgumentList = { "-e", "-c", task.Script } };
        startInfo.WorkingDirectory = task.Root;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException(
                                $"Failed to start the setup shell for mount '{task.MountId}'.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (await stdout is { Length: > 0 } output)
            logger.LogInformation("Workspace setup '{MountId}': {Output}", task.MountId, output.Trim());
        if (await stderr is { Length: > 0 } error)
            logger.LogWarning("Workspace setup '{MountId}' stderr: {Error}", task.MountId, error.Trim());
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Workspace setup for mount '{task.MountId}' exited with {process.ExitCode}.");
    }

    private static Dictionary<string, string> SnapshotEnvironment()
    {
        var snapshot = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            if (entry is { Key: string key, Value: string value })
                snapshot[key] = value;
        return snapshot;
    }
}
