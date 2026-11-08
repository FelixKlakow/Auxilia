using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Dummy workflow for run checkpoints: it keeps a session folder and a summary file in its output
/// directory and saves both after each of two "turns" — after planting a link to the runner's own
/// environment in the folder, which must never leave with it — then, when the run context asks
/// (<c>FAIL=true</c>), fails, so the last save has to outlive the ending.
/// </summary>
public static class CheckpointWorkflow
{
    public const string WorkflowName = "checkpoint-workflow";
    public const string SessionCheckpoint = "session";
    public const string SummaryCheckpoint = "summary";

    /// <summary>Hours the workflow asks its checkpoints be kept.</summary>
    public const double DeclaredLifetimeHours = 2;

    public static Task RunAsync(string[] args) => Declare().WithApplication(ExecuteAsync).Run(args);

    /// <summary>The schema the platform registers the type with — the same declarations the image runs.</summary>
    public static WorkflowSchema Schema() => ((WorkflowBuilder)Declare()).BuildSchema();

    private static IWorkflowBuilder Declare() =>
        WorkflowBuilder
            .Create(WorkflowName)
            .DeclaresCheckpoint(SessionCheckpoint, "session", "the session folder to hand back",
                TimeSpan.FromHours(DeclaredLifetimeHours))
            .DeclaresCheckpoint(SummaryCheckpoint, "summary.md", "what happened so far");

    private static async Task ExecuteAsync(IServiceProvider provider, CancellationToken ct)
    {
        var output = System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.OutputDirectory)
                     ?? throw new InvalidOperationException("no output directory");
        var session = Path.Combine(output, "session");
        Directory.CreateDirectory(Path.Combine(session, "turns"));
        var checkpoints = provider.GetRequiredService<IRunCheckpoints>();

        for (var turn = 1; turn <= 2; turn++)
        {
            await File.WriteAllTextAsync(Path.Combine(session, "turns", $"{turn}.md"), $"turn {turn}", ct);
            await File.WriteAllTextAsync(Path.Combine(session, "state.json"), $$"""{"turn":{{turn}}}""", ct);
            await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), $"{turn} turn(s) so far", ct);
            if (turn == 2)
                // Resolved where the RUNNER reads the folder: its own environment, never the run's.
                File.CreateSymbolicLink(Path.Combine(session, "planted"), "/proc/self/environ");
            var saved = await checkpoints.SaveAsync(SessionCheckpoint, ct);
            await checkpoints.SaveAsync(SummaryCheckpoint, ct);
            Console.WriteLine($"[CheckpointWorkflow] turn {turn} saved as v{saved.Version}");
        }

        if (System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__FAIL") == "true")
            throw new InvalidOperationException("failing on purpose — the last checkpoint must outlive this");
    }
}
