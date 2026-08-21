using System.Net.Http.Json;
using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SystemTestSuite.PodFabric;

/// <summary>
/// Pod re-adoption on real Docker: a pod-controlled run survives a full runner restart —
/// the restarted runner re-adopts the workflow container AND rebuilds its pod-control state
/// (envelope, configuration-pinned base map, pod network identity) from persisted records,
/// so a runtime spawn issued only AFTER the restart still succeeds and the pod dies with
/// the run as usual.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class PodReadoptionSystemTests
{
    private static HttpClient Client => PodFabricEnvironment.CoreApiClient;
    private static IMessageBusClient Bus => PodFabricEnvironment.MessageBusClient;

    [Test]
    [CancelAfter(600_000)]
    public async Task PodControlledRun_SurvivesRunnerRestart_AndSpawnsAfterwards(
        CancellationToken cancellationToken)
    {
        // --- Dispatch; the workflow parks until the proceed marker appears. ---
        var terminal = new TaskCompletionSource<WorkflowStateMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        const string stateExchange = "workflow.state";
        await Bus.DeclareExchangeAsync(stateExchange, cancellationToken);
        // The Core's RunId and the runner's instance id are ALIASED, not equal — host-side
        // run roots and bus state messages carry the runner's id, discovered below.
        Guid instanceId = Guid.Empty;
        await using var subscription = await Bus.SubscribeToExchangeAsync<WorkflowStateMessage>(
            stateExchange, (msg, _) =>
            {
                if (msg.WorkflowInstanceId == instanceId)
                    terminal.TrySetResult(msg);
                return Task.CompletedTask;
            }, cancellationToken);

        var existingRuns = Directory.GetDirectories(PodFabricEnvironment.RunOutputDir)
            .Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runResponse = await Client.PostAsJsonAsync("/api/runs", new RunRequest(
            WorkflowType: PodFabricEnvironment.RestartWorkflowType,
            Context: new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = PodFabricEnvironment.RestartWorkflowType,
                ["pod-bases"] = PodFabricEnvironment.SpawnableBaseName
            }), cancellationToken);
        runResponse.EnsureSuccessStatusCode();

        // The application is executing once its "ready" file appears on the shared bind —
        // the per-instance output DIRECTORY exists from dispatch, so the directory alone
        // would race the SDK handshake. The new directory's name IS the runner's instance id.
        string? instanceDirName = null;
        try
        {
            await WaitForAsync(() =>
                (instanceDirName = Directory.GetDirectories(PodFabricEnvironment.RunOutputDir)
                    .Select(Path.GetFileName)
                    .FirstOrDefault(name => name is not null
                                            && !existingRuns.Contains(name)
                                            && File.Exists(Path.Combine(
                                                PodFabricEnvironment.RunOutputDir, name, "ready"))))
                is not null,
                TimeSpan.FromSeconds(180), cancellationToken);
        }
        catch (Exception)
        {
            Assert.Fail("The workflow application never came up.\n--- Runner log tail ---\n"
                        + await PodFabricEnvironment.RunnerLogTailAsync(8000));
        }
        instanceId = Guid.ParseExact(instanceDirName!, "N");
        var outputDir = Path.Combine(PodFabricEnvironment.RunOutputDir, instanceDirName!);

        // --- The restart: stop the runner, start it again, wait for the re-adoption claim. ---
        await PodFabricEnvironment.Runner.StopAsync(cancellationToken);
        await PodFabricEnvironment.Runner.StartAsync(cancellationToken);
        await WaitForAsync(() => PodFabricEnvironment.RunnerLogTailAsync()
                .GetAwaiter().GetResult().Contains("Adopted=1"),
            TimeSpan.FromSeconds(120), cancellationToken);

        // --- Only NOW may the workflow spawn: pod-control state must have been rebuilt. ---
        await File.WriteAllTextAsync(Path.Combine(outputDir, "proceed"), "go", cancellationToken);

        WorkflowStateMessage state;
        try
        {
            state = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(240), cancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail("The run never reached a terminal state after the restart.\n--- Runner log tail ---\n"
                        + await PodFabricEnvironment.RunnerLogTailAsync());
            return;
        }
        Assert.That(state.State, Is.EqualTo(WorkflowState.Success),
            $"Run ended {state.State}: {state.ErrorMessage}\n--- Runner log tail ---\n"
            + await PodFabricEnvironment.RunnerLogTailAsync());

        // --- The post-restart spawn really happened and was torn down with the run. ---
        IReadOnlyList<ArtifactDto> artifacts = [];
        await WaitForAsync(() =>
        {
            artifacts = Client.GetFromJsonAsync<PagedResult<ArtifactDto>>(
                    $"/api/artifacts?runId={instanceId}&take=50", cancellationToken)
                .GetAwaiter().GetResult()!.Items;
            var types = artifacts.Select(a => a.ArtifactType).ToHashSet();
            return types.Contains("restart-report") && types.Contains("companion-log-machine-pr");
        }, TimeSpan.FromSeconds(120), cancellationToken);

        var reportPath = Path.Combine(PodFabricEnvironment.ArtifactsDir,
            artifacts.Single(a => a.ArtifactType == "restart-report").Id.ToString("N"));
        var report = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllBytesAsync(reportPath, cancellationToken));
        Assert.That(report.GetProperty("spawnedEndpoint").GetString(), Is.EqualTo("machine-pr:9000"),
            "the runtime spawn after the restart must have delivered a ready machine");
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(1000, ct);
        }
        Assert.That(condition(), Is.True, $"condition not met within {timeout.TotalSeconds:0}s");
    }
}
