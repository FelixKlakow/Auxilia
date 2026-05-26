using System.Diagnostics;
using Auxilia.Workflows.SourceControl;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Minimal dummy workflow used by the WorkflowDispatch system tests.
/// Declares one source-control slot (exercises the full registration / config-resolution path),
/// then clones nothing — it creates a fresh local git repo, writes <c>test.txt</c>, and commits.
///
/// The repo path is read from the <c>WORKFLOW_CONTEXT__REPO_PATH</c> env var.  When the var is
/// absent the workflow creates its own temp directory so it can still run in standalone tests.
/// </summary>
public static class SimpleGitCommitWorkflow
{
    public const string WorkflowName = "simple-git-commit-workflow";

    /// <summary>Image tag used when building the dummy-workflows Docker image for system tests.</summary>
    public const string ImageName = "auxilia-dummy-workflows:system-test";

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder
            .Create(WorkflowName)
            .RequiresSourceControl(
                "source-control",
                new SourceControlCapabilities { RequiredPermissions = [Permission.Write] },
                description: "Target repository for the smoke-test commit.")
            .WithRunBody(ExecuteAsync)
            .Run(args);

    private static async Task ExecuteAsync(IServiceProvider _, CancellationToken ct)
    {
        // Prefer an externally-provided repo path (e.g. a bind-mounted volume).
        // Fall back to a fresh temp directory so the workflow is self-contained.
        var repoPath = System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__REPO_PATH")
                       ?? Path.Combine(Path.GetTempPath(), $"auxilia-wf-repo-{Guid.NewGuid():N}");

        Directory.CreateDirectory(repoPath);

        // Ensure git identity is set for this repo (required for commits in clean environments).
        RunGit(repoPath, "init");
        RunGit(repoPath, "config", "user.email", "system-test@auxilia.local");
        RunGit(repoPath, "config", "user.name", "Auxilia System Test");

        // Write the file.
        var filePath = Path.Combine(repoPath, "test.txt");
        await File.WriteAllTextAsync(filePath,
            $"hello from Auxilia system test — {DateTime.UtcNow:O}", ct);

        // Stage and commit.
        RunGit(repoPath, "add", "test.txt");
        RunGit(repoPath, "commit", "-m", "test: add test.txt via simple-git-commit-workflow");

        Console.WriteLine($"[SimpleGitCommitWorkflow] Committed test.txt in {repoPath}");
    }

    private static void RunGit(string workingDirectory, params string[] gitArgs)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in gitArgs)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start git process.");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git {string.Join(' ', gitArgs)} failed (exit {process.ExitCode}): {stderr}");
        }
    }
}



