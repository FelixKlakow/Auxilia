using System.Reflection;
using System.Text.Json;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Egress;
using Auxilia.Core.Runner.Workflows.Pods;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Auxilia.DockerGuard.Tests;

/// <summary>
/// The contract between the runner and its guard: every create request the runner builds — its
/// workflow containers (baked image and package launch), egress gateways, companions, run and
/// pod networks, pod volumes — serialized EXACTLY as Docker.DotNet puts it on the wire, passes the
/// policy. A runner change the guard would refuse fails here, not in production.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunnerCompatibilityTests
{
    private static readonly Guid Instance = Guid.NewGuid();
    private static readonly string WorkspaceRoot = "/var/lib/auxilia/workspaces";
    private static readonly string OutputRoot = "/var/lib/auxilia/run-output";
    /// <summary>Where a Linux runner's temp directory (its extracted packages) lives on the daemon host.</summary>
    private static readonly string PackageRoot = "/var/lib/auxilia/packages";

    private FakeDockerObjects _objects = null!;
    private DockerGuardSettings _settings = null!;

    [SetUp]
    public void SetUp()
    {
        _objects = new FakeDockerObjects();
        var plan = new EgressPlan(Instance, "", [], null);
        _objects.Networks[plan.NetworkName] = new DockerNetwork("run", plan.NetworkName, FakeDockerObjects.Egress);
        _objects.Networks[$"auxilia-pod-{Instance:N}"] = new DockerNetwork("pod", $"auxilia-pod-{Instance:N}", FakeDockerObjects.Companion);
        _objects.Volumes[$"auxilia-pod-{Instance:N}-logs"] = FakeDockerObjects.Companion;
        _settings = new DockerGuardSettings
        {
            BindRoots = [WorkspaceRoot, OutputRoot, PackageRoot],
            OutboundNetworks = ["operator-net"],
        };
    }

    [Test]
    public async Task TheRunnersWorkflowContainer_BakedImage_PassesTheGuard()
    {
        var launcher = new DockerWorkflowLauncherSettings { NetworkName = "operator-net", RabbitMqHost = "rabbitmq" };
        var request = new WorkflowLaunchRequest(string.Empty, new Dictionary<string, string> { ["A"] = "1" }, [])
        {
            DockerImageUri = "registry.example/team/wf@sha256:" + new string('a', 64),
            InstanceId = Instance,
            OutputDirectoryBind = $"{OutputRoot}/{Instance:N}",
            WorkspaceDirectoryBind = $"{WorkspaceRoot}/{Instance:N}",
            PublishTerminalPort = 7681,
            Egress = new EgressPlan(Instance, "", [], 7681),
        };
        var parameters = DockerWorkflowLauncher.BuildBakedImageContainerParameters(request, launcher);
        DockerWorkflowLauncher.ApplyConfinement(parameters.HostConfig, launcher, linux: true, daemonCpus: 8, "/app", parameters.Labels);

        var decision = await DecideCreateAsync(parameters);
        Assert.That(decision.Allowed, Is.True, decision.Reason);
        Assert.That(decision.Body, Is.Not.Null, "its workspace and output binds are carried out beneath their roots");
    }

    [Test]
    public async Task TheRunnersWorkflowContainer_PackageLaunch_PassesTheGuard()
    {
        var extracted = Path.Combine(Path.GetTempPath(), $"auxilia-wf-{Instance:N}");
        Directory.CreateDirectory(extracted);
        try
        {
            File.WriteAllText(Path.Combine(extracted, "package-manifest.json"),
                """{"executableRelativePath":"wf.dll","files":[]}""");
            var launcher = new DockerWorkflowLauncherSettings { NetworkName = "operator-net" };
            var request = new WorkflowLaunchRequest(extracted, new Dictionary<string, string>(), [])
            {
                InstanceId = Instance,
                Egress = new EgressPlan(Instance, "", [], null),
            };
            var parameters = DockerWorkflowLauncher.BuildCreateContainerParameters(request, launcher);
            // The package bind as a Linux runner sends it (this test may run on Windows).
            parameters.HostConfig.Binds = parameters.HostConfig.Binds
                .Select(bind => bind.Replace(extracted, $"{PackageRoot}/{Path.GetFileName(extracted)}"))
                .ToList();

            var decision = await DecideCreateAsync(parameters);
            Assert.That(decision.Allowed, Is.True, decision.Reason);
        }
        finally
        {
            Directory.Delete(extracted, recursive: true);
        }
    }

    [Test]
    public async Task TheRunnersEgressGateway_PassesTheGuard()
    {
        var parameters = DockerEgressHost.BuildGatewayParameters(
            new EgressPlan(Instance, "api.example.com", ["5672=rabbitmq:5672/amqp"], 7681),
            new DockerWorkflowLauncherSettings { NetworkName = "operator-net" });

        var decision = await DecideCreateAsync(parameters);
        Assert.That(decision, Is.EqualTo(GuardDecision.Allow), decision.Reason);
    }

    [Test]
    public async Task TheRunnersCompanion_PassesTheGuard()
    {
        var companion = new PlannedCompanion("db", "db-1", "postgres@sha256:" + new string('b', 64),
            new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "x" }, null, 256, 0.5,
            [$"auxilia-pod-{Instance:N}-logs:/logs"]);
        var parameters = DockerPodHost.BuildCompanionParameters(companion, Instance, $"auxilia-pod-{Instance:N}");

        var decision = await DecideCreateAsync(parameters);
        Assert.That(decision, Is.EqualTo(GuardDecision.Allow), decision.Reason);
    }

    [Test]
    public async Task TheRunnersNetworksAndVolumes_PassTheGuard()
    {
        var policy = new DockerApiPolicy(_settings, _objects);
        var network = Serialize(new NetworksCreateParameters
        {
            Name = $"auxilia-run-{Instance:N}",
            Internal = true,
            Labels = new Dictionary<string, string> { ["auxilia.egress"] = "1", ["auxilia.instance-id"] = Instance.ToString() }
        });
        var volume = Serialize(new VolumesCreateParameters
        {
            Name = $"auxilia-pod-{Instance:N}-logs",
            Labels = new Dictionary<string, string> { ["auxilia.companion"] = "1" }
        });
        var membership = Serialize(new NetworkConnectParameters { Container = "gateway" });

        Assert.Multiple(async () =>
        {
            Assert.That(await policy.DecideAsync("POST", "/v1.47/networks/create", new Dictionary<string, string[]>(), network, CancellationToken.None),
                Is.EqualTo(GuardDecision.Allow));
            Assert.That(await policy.DecideAsync("POST", "/v1.47/volumes/create", new Dictionary<string, string[]>(), volume, CancellationToken.None),
                Is.EqualTo(GuardDecision.Allow));
            Assert.That(await policy.DecideAsync("POST", "/v1.47/networks/operator-net/connect", new Dictionary<string, string[]>(), membership, CancellationToken.None),
                Is.EqualTo(GuardDecision.Allow));
        });
    }

    private Task<GuardDecision> DecideCreateAsync(CreateContainerParameters parameters)
        => new DockerApiPolicy(_settings, _objects).DecideAsync(
            "POST", "/v1.47/containers/create", new Dictionary<string, string[]>(), Serialize(parameters), CancellationToken.None);

    /// <summary>The request body as Docker.DotNet's own serializer writes it.</summary>
    private static JsonElement Serialize<T>(T value)
    {
        var serializerType = typeof(DockerClient).Assembly.GetType("Docker.DotNet.JsonSerializer", throwOnError: true)!;
        var serializer = Activator.CreateInstance(serializerType, nonPublic: true)!;
        var method = serializerType.GetMethod("SerializeObject", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .MakeGenericMethod(typeof(T));
        var json = (string)method.Invoke(serializer, [value])!;
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
