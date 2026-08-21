using System.Text.Json;
using Auxilia.Workflows;
using Auxilia.Workflows.Companions;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Testing.DummyWorkflows;

/// <summary>
/// Proves pod re-adoption across a runner restart: the workflow parks until the TEST drops a
/// <c>proceed</c> marker into its output directory — which the test only does AFTER stopping
/// and restarting the runner container — then performs a runtime spawn. The spawn can only
/// succeed if the restarted runner rebuilt the run's pod-control state (envelope, pinned base
/// map, pod network identity) from its persisted records.
/// </summary>
public static class PodRestartWorkflow
{
    public const string WorkflowName = "pod-restart-workflow";

    public static Task RunAsync(string[] args) =>
        WorkflowBuilder.Create(WorkflowName)
            .RequiresPodControl(1, "One machine, spawned only after the runner restarted")
            .DeclaresOutput("restart-report", "report.json", "Post-restart spawn verdict")
            .WithApplication(ExecuteAsync)
            .Run(args);

    private static async Task ExecuteAsync(IServiceProvider provider, CancellationToken ct)
    {
        var pod = provider.GetService<IPodController>()
                  ?? throw new InvalidOperationException("no IPodController — pod control was not delivered");
        var outputDir = System.Environment.GetEnvironmentVariable(WorkflowEnvironmentVariables.OutputDirectory)
                        ?? throw new InvalidOperationException("no output directory");
        Directory.CreateDirectory(outputDir);
        // The test must not restart the runner before the SDK handshake is fully behind us —
        // this file is the "application is executing" signal (the output DIRECTORY exists
        // from the moment of dispatch; only the app writes files into it).
        await File.WriteAllTextAsync(Path.Combine(outputDir, "ready"), "up", ct);

        // Park until the test says the runner restart is behind us.
        var marker = Path.Combine(outputDir, "proceed");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(300);
        while (!File.Exists(marker))
        {
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException("the proceed marker never appeared");
            await Task.Delay(1000, ct);
        }

        var spawned = await pod.SpawnAsync(new CompanionSpec("machine-pr", "sim-machine")
        {
            EnvironmentVariables = new Dictionary<string, string> { ["ROLE"] = "machine" },
            Readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, 9000)
        }, ct);

        await File.WriteAllTextAsync(Path.Combine(outputDir, "report.json"),
            JsonSerializer.Serialize(new { spawnedEndpoint = spawned.Endpoint }), ct);
    }
}
