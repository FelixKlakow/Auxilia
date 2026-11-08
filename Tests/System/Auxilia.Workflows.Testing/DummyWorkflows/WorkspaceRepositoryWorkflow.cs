using Auxilia.Workflows;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Verifies a per-run repository was cloned into the workspace and bind-mounted. Reads
/// <c>WORKFLOW_CONTEXT__EXPECTED_REPO_FILE</c> (relative to the mounted workspace) and its expected
/// content, failing the run when the file is absent or differs — so reaching Success proves the
/// Workspace Manager resolved the repo's auth connector, cloned the (authenticated) repo, and mounted
/// it. It declares no slots: the repository comes from the run command, not the workflow schema.
/// </summary>
public static class WorkspaceRepositoryWorkflow
{
    public const string WorkflowName = "workspace-repository-workflow";

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder.Create(WorkflowName).WithApplication(ExecuteAsync).Run(args);

    private static async Task ExecuteAsync(IServiceProvider _, CancellationToken ct)
    {
        var workspace = System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.WorkspaceDirectory)
                        ?? throw new InvalidOperationException(
                            "The workspace directory env var is not set — no repository was mounted for this run.");
        var relative = System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__EXPECTED_REPO_FILE")
                       ?? throw new InvalidOperationException("WORKFLOW_CONTEXT__EXPECTED_REPO_FILE is not set.");
        var expectedContent = System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__EXPECTED_REPO_CONTENT");

        var path = Path.Combine(workspace, relative);
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Expected repository file not found at '{path}' — the repository was not cloned/mounted.");

        if (expectedContent is not null)
        {
            var actual = (await File.ReadAllTextAsync(path, ct)).Trim();
            if (actual != expectedContent.Trim())
                throw new InvalidOperationException($"Repository file content mismatch at '{path}'.");
        }

        // A pushable workspace holds the credential NOWHERE — no file, no git configuration.
        if (System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__FORBIDDEN_SECRET") is { Length: > 0 } secret)
        {
            var needle = System.Text.Encoding.UTF8.GetBytes(secret);
            foreach (var file in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
                if (File.ReadAllBytes(file).AsSpan().IndexOf(needle) >= 0)
                    throw new InvalidOperationException($"The workspace holds the credential in '{file}'.");
            Console.WriteLine("[WorkspaceRepositoryWorkflow] No credential anywhere in the workspace.");
        }

        // ...and a push still lands: committed and pushed as the agent identity (workspace git
        // runs sandboxed) to its origin — the push gateway, the only holder of the credential.
        if (System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__PUSH_MARKER") is { Length: > 0 } marker)
        {
            var repository = Path.GetDirectoryName(path)!;
            await File.WriteAllTextAsync(Path.Combine(repository, "pushed.txt"), marker, ct);
            await AgentGitAsync(repository, ct, "add", "pushed.txt");
            await AgentGitAsync(repository, ct, "commit", "-m", marker);
            await AgentGitAsync(repository, ct, "push", "origin", $"HEAD:refs/heads/{marker}");
            Console.WriteLine($"[WorkspaceRepositoryWorkflow] Pushed {marker}.");
        }

        Console.WriteLine($"[WorkspaceRepositoryWorkflow] Verified mounted repository file {path}.");
    }

    private static async Task AgentGitAsync(string repository, CancellationToken ct, params string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repository);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(Sandbox.AgentSandbox.Confine(startInfo))!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {await stderr} {await stdout}");
    }
}
