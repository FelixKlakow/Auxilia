using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Auxilia.SystemTestSuite.PodFabric;

/// <summary>
/// The full run-pod worked example on real Docker (test-fabric design §A): a pod of
/// rabbit + two central pieces + input-scaled machines materializes on a private per-run
/// network, the workflow GROWS and SHRINKS the fleet at runtime through the pod controller
/// (configuration-pinned base), the availability handshake tracks every change, and the
/// run ships logs.zip + report.json — with every companion's log tail persisted and the
/// whole pod (containers, network, volumes) gone after the run.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class PodScenarioSystemTests
{
    private static HttpClient Client => PodFabricEnvironment.CoreApiClient;
    private static IMessageBusClient Bus => PodFabricEnvironment.MessageBusClient;

    [Test]
    [CancelAfter(600_000)]
    public async Task DistributedSystemScenario_DynamicFleet_RunsToSuccess_WithLogArtifacts(
        CancellationToken cancellationToken)
    {
        // --- Dispatch: 2 declared machines; the configuration pins the spawnable base. ---
        await using var terminal = await SubscribeTerminalAsync(cancellationToken);
        var run = new RunRequest(
            WorkflowType: PodFabricEnvironment.WorkflowType,
            Context: new Dictionary<string, string>
            {
                ["WORKFLOW_NAME"] = PodFabricEnvironment.WorkflowType,
                ["machines"] = "2",
                ["pod-bases"] = PodFabricEnvironment.SpawnableBaseName
            });
        var runResponse = await Client.PostAsJsonAsync("/api/runs", run, cancellationToken);
        runResponse.EnsureSuccessStatusCode();

        // --- Mid-run: the pod network must exist and be --internal (zero egress). ---
        using var midRunDocker = new DockerClientConfiguration().CreateClient();
        var podNetworkFilter = new NetworksListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { ["auxilia.companion=1"] = true }
            }
        };
        await WaitForAsync(() =>
            midRunDocker.Networks.ListNetworksAsync(podNetworkFilter, cancellationToken)
                .GetAwaiter().GetResult().Count > 0,
            TimeSpan.FromSeconds(180), cancellationToken);
        var podNetworks = await midRunDocker.Networks.ListNetworksAsync(podNetworkFilter, cancellationToken);
        Assert.That(podNetworks, Is.All.Matches<NetworkResponse>(n => n.Internal),
            "every pod network must be --internal — companions get zero egress");

        WorkflowStateMessage state;
        try
        {
            state = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(420), cancellationToken);
        }
        catch (TimeoutException)
        {
            Assert.Fail("The scenario run never reached a terminal state.\n--- Runner log tail ---\n"
                        + await PodFabricEnvironment.RunnerLogTailAsync());
            return;
        }
        Assert.That(state.State, Is.EqualTo(WorkflowState.Success),
            $"Scenario run ended {state.State}: {state.ErrorMessage}\n--- Runner log tail ---\n"
            + await PodFabricEnvironment.RunnerLogTailAsync());
        var instanceId = state.WorkflowInstanceId;

        // --- Artifacts: outputs + every torn-down companion's log (incl. the runtime-spawned
        //     machine-r2; machine-r1 was stopped mid-run, so no teardown capture exists). ---
        IReadOnlyList<ArtifactDto> artifacts = [];
        await WaitForAsync(() =>
        {
            artifacts = QueryArtifactsAsync(instanceId, cancellationToken).GetAwaiter().GetResult();
            var types = artifacts.Select(a => a.ArtifactType).ToHashSet();
            return types.Contains("logs-zip") && types.Contains("scenario-report")
                   && types.Contains("companion-log-rabbit")
                   && types.Contains("companion-log-fleet-manager")
                   && types.Contains("companion-log-coordinator")
                   && types.Contains("companion-log-machine-1")
                   && types.Contains("companion-log-machine-r2");
        }, TimeSpan.FromSeconds(120), cancellationToken);

        var artifactTypes = artifacts.Select(a => a.ArtifactType).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(artifactTypes, Does.Contain("logs-zip"));
            Assert.That(artifactTypes, Does.Contain("scenario-report"));
            Assert.That(artifactTypes, Does.Contain("companion-log-rabbit"));
            Assert.That(artifactTypes, Does.Contain("companion-log-fleet-manager"));
            Assert.That(artifactTypes, Does.Contain("companion-log-coordinator"));
            Assert.That(artifactTypes, Does.Contain("companion-log-machine-1"));
            Assert.That(artifactTypes, Does.Contain("companion-log-machine-2"));
            Assert.That(artifactTypes, Does.Contain("companion-log-machine-r2"),
                "the runtime-spawned machine dies with the run like any declared companion");
            Assert.That(artifactTypes, Does.Not.Contain("companion-log-machine-r1"),
                "a mid-run stop removes the companion without a teardown capture");
        });

        // --- Payloads (shared artifact root): the report's counts prove the whole handshake. ---
        var report = JsonSerializer.Deserialize<JsonElement>(
            PayloadOf(artifacts.Single(a => a.ArtifactType == "scenario-report")));
        Assert.Multiple(() =>
        {
            Assert.That(report.GetProperty("declared").GetInt32(), Is.EqualTo(2));
            Assert.That(report.GetProperty("afterSpawn").GetInt32(), Is.EqualTo(4),
                "two runtime spawns must have become available through manager + coordinator");
            Assert.That(report.GetProperty("afterStop").GetInt32(), Is.EqualTo(3),
                "stopping a spawned machine must drop it from availability");

            // Isolation, proven by real TCP dials from INSIDE the pod (the coordinator):
            // a pod peer answers, the platform bus and the internet must not.
            var egress = report.GetProperty("egress");
            Assert.That(egress.GetProperty("podPeer").GetString(), Is.EqualTo("OPEN"),
                "the prober itself must work — pod peers stay mutually reachable");
            Assert.That(egress.GetProperty("platformBus").GetString(), Does.StartWith("CLOSED"),
                "a companion must never reach the platform's RabbitMQ");
            Assert.That(egress.GetProperty("internet").GetString(), Does.StartWith("CLOSED"),
                "a companion must never reach the internet");
        });

        using var zip = new ZipArchive(new MemoryStream(
            PayloadOf(artifacts.Single(a => a.ArtifactType == "logs-zip"))));
        var entries = zip.Entries.Select(e => e.Name).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(entries, Does.Contain("fleet-manager.log"));
            Assert.That(entries, Does.Contain("coordinator.log"));
            Assert.That(entries.Count(e => e.StartsWith("machine-", StringComparison.Ordinal)),
                Is.GreaterThanOrEqualTo(3),
                "declared and runtime-spawned machines all logged onto the shared pod volume");
        });

        // --- The pod dies with the run: no companions, no per-run network, no volumes. ---
        using var docker = new DockerClientConfiguration().CreateClient();
        var labelFilter = new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool>
            {
                ["auxilia.companion=1"] = true,
                [$"auxilia.instance-id={instanceId:D}"] = true
            }
        };
        await WaitForAsync(() =>
            docker.Containers.ListContainersAsync(
                    new ContainersListParameters { All = true, Filters = labelFilter },
                    cancellationToken).GetAwaiter().GetResult().Count == 0,
            TimeSpan.FromSeconds(60), cancellationToken);
        var companions = await docker.Containers.ListContainersAsync(
            new ContainersListParameters { All = true, Filters = labelFilter }, cancellationToken);
        var networks = await docker.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["name"] = new Dictionary<string, bool> { [$"auxilia-pod-{instanceId:N}"] = true }
            }
        }, cancellationToken);
        var volumes = await docker.Volumes.ListAsync(
            new VolumesListParameters { Filters = labelFilter }, cancellationToken);
        Assert.Multiple(() =>
        {
            Assert.That(companions, Is.Empty, "every companion must die with the run");
            Assert.That(networks, Is.Empty, "the per-run pod network must die with the run");
            Assert.That(volumes.Volumes ?? [], Is.Empty, "the per-run pod volumes must die with the run");
        });
    }

    private static byte[] PayloadOf(ArtifactDto artifact)
    {
        var path = Path.Combine(PodFabricEnvironment.ArtifactsDir, artifact.Id.ToString("N"));
        Assert.That(File.Exists(path), $"payload of {artifact.ArtifactType} missing at {path}");
        return File.ReadAllBytes(path);
    }

    private static async Task<IReadOnlyList<ArtifactDto>> QueryArtifactsAsync(
        Guid instanceId, CancellationToken ct)
    {
        var page = await Client.GetFromJsonAsync<PagedResult<ArtifactDto>>(
            $"/api/artifacts?runId={instanceId}&take=50", ct);
        return page!.Items;
    }

    private static async Task<TerminalWaiter> SubscribeTerminalAsync(CancellationToken ct)
    {
        const string stateExchange = "workflow.state";
        await Bus.DeclareExchangeAsync(stateExchange, ct);
        var tcs = new TaskCompletionSource<WorkflowStateMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = await Bus.SubscribeToExchangeAsync<WorkflowStateMessage>(
            stateExchange, (msg, _) => { tcs.TrySetResult(msg); return Task.CompletedTask; }, ct);
        return new TerminalWaiter(tcs.Task, subscription);
    }

    private sealed record TerminalWaiter(
        Task<WorkflowStateMessage> Task, IAsyncDisposable Subscription) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Subscription.DisposeAsync();
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
