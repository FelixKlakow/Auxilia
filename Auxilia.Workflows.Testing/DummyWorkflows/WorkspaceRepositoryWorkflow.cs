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

        Console.WriteLine($"[WorkspaceRepositoryWorkflow] Verified mounted repository file {path}.");
    }
}
