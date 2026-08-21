using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Auxilia.Core.Contracts;
using Auxilia.Messaging;
using Auxilia.Workflows;
using Auxilia.Workflows.Companions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.PodFabric;

/// <summary>
/// System environment for the run-pod scenario tests (test-fabric design §A worked example).
///
/// Topology: RabbitMQ (platform bus) + Core.Api + Core.Runner as usual, PLUS a throwaway
/// local Docker registry whose only job is minting REAL repo digests for the in-test-built
/// companion images — companion declarations are digest-pinned by design, and a
/// <c>docker push</c> to the local registry gives the daemon a resolvable
/// <c>name@sha256:…</c> reference without any pull ever happening.
///
/// The pod-scenario workflow type is registered over REST with a HAND-BUILT schema carrying
/// those digests (rabbit + machines + fleet-manager + coordinator + the pod-control
/// envelope), approved, and its runtime-spawnable base (<c>sim-machine</c>) enters the
/// environment-base catalog with its image reference. The runner shares its artifact
/// payload root with the host so tests can open logs.zip / report.json directly.
/// </summary>
[SetUpFixture]
public class PodFabricEnvironment
{
    internal const string DummyWorkflowsImageName = "auxilia-dummy-workflows:system-test";
    internal const string RunnerImageName = "auxilia-core-runner:system-test";
    internal const string CoreApiImageName = "auxilia-core-api:system-test";
    internal const string SimStackImageName = "auxilia-sim-stack:system-test";
    internal const string WorkflowType = "pod-scenario-workflow";
    internal const string RestartWorkflowType = "pod-restart-workflow";
    internal const string BootstrapApiKey = "aux-system-test-key-podfabric-0123456";
    internal const string SpawnableBaseName = "sim-machine";

    private const string RabbitMqAlias = "rabbitmq";
    private const string RabbitMqImage = "rabbitmq:3.13-management";
    private const string CoreApiAlias = "core-api";
    private const string ContainerRunOutput = "/run-output";
    private const string ContainerWorkspaces = "/workspaces";
    private const string ContainerArtifacts = "/artifacts";

    private static readonly string NetworkName =
        $"auxilia-podfab-{Guid.NewGuid():N}".Substring(0, 30);

    private string? _registryName;
    private IContainer _runner = null!;
    private IContainer _coreApi = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private INetwork _network = null!;
    private string _runOutputDir = null!;
    private string _workspaceDir = null!;

    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    public static HttpClient CoreApiClient { get; private set; } = null!;
    public static IContainer Runner { get; private set; } = null!;

    /// <summary>Host directory the runner persists artifact payloads into (file name = id "N").</summary>
    public static string ArtifactsDir { get; private set; } = null!;

    /// <summary>Host side of the runner's run-output bind (per-run subfolder = id "N").</summary>
    public static string RunOutputDir { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Sequential on purpose — parallel docker builds have wedged Docker Desktop daemons.
        await TestImages.BuildImageAsync(RunnerImageName, "Source/Platform/Auxilia.Core.Runner/Dockerfile");
        await TestImages.BuildImageAsync(CoreApiImageName, "Source/Platform/Auxilia.Core.Api/Dockerfile");
        await TestImages.BuildImageAsync(DummyWorkflowsImageName, "Tests/System/Auxilia.Workflows.Testing/Dockerfile");
        await TestImages.BuildImageAsync(SimStackImageName, "Tests/System/Auxilia.SimStack/Dockerfile");

        _network = new NetworkBuilder().WithName(NetworkName).Build();
        await _network.CreateAsync();

        _rabbitMq = new RabbitMqBuilder(RabbitMqImage)
            .WithUsername("guest").WithPassword("guest")
            .WithNetwork(_network).WithNetworkAliases(RabbitMqAlias)
            .Build();
        await _rabbitMq.StartAsync();

        // Digest minting: push the companion images once through a throwaway local registry —
        // the push stamps a RepoDigest onto the local daemon, so the runner's
        // InspectImage(name@sha256:…) resolves WITHOUT pulling (the registry could even die).
        // The registry runs with a DAEMON-assigned host port (-p 0:5000): on Docker Desktop
        // the daemon can reach ITS OWN ephemeral bindings via localhost, while ports mapped
        // by the test host are not visible from the daemon's side ("context deadline
        // exceeded" on push).
        _registryName = $"auxilia-podfab-registry-{Guid.NewGuid():N}"[..30];
        await RunDockerAsync($"run -d --name {_registryName} -p 0:5000 registry:2");
        var portLine = (await RunDockerAsync($"port {_registryName} 5000/tcp"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        var registryPort = ushort.Parse(portLine[(portLine.LastIndexOf(':') + 1)..]);
        var simStackDigestRef = await MintDigestAsync(SimStackImageName, registryPort, "sim-stack");
        var rabbitDigestRef = await MintDigestAsync(RabbitMqImage, registryPort, "pod-rabbit");

        RunOutputDir = _runOutputDir =
            Path.Combine(Path.GetTempPath(), $"auxilia-podfab-output-{Guid.NewGuid():N}");
        _workspaceDir = Path.Combine(Path.GetTempPath(), $"auxilia-podfab-workspaces-{Guid.NewGuid():N}");
        ArtifactsDir = Path.Combine(Path.GetTempPath(), $"auxilia-podfab-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runOutputDir);
        Directory.CreateDirectory(_workspaceDir);
        Directory.CreateDirectory(ArtifactsDir);

        const string dockerSocket = "/var/run/docker.sock";
        Runner = _runner = new ContainerBuilder(RunnerImageName)
            .WithNetwork(_network)
            .WithBindMount(dockerSocket, dockerSocket)
            .WithBindMount(_runOutputDir, ContainerRunOutput)
            .WithBindMount(_workspaceDir, ContainerWorkspaces)
            // Shared payload root: the test opens what the runner persists.
            .WithBindMount(ArtifactsDir, ContainerArtifacts)
            .WithEnvironment("ArtifactStore__PayloadRoot", ContainerArtifacts)
            .WithEnvironment("RabbitMq__Host", RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port", "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("WorkflowDispatcher__CoreApiBaseAddress", $"http://{CoreApiAlias}:8080")
            .WithEnvironment("WorkflowLauncher__NetworkName", NetworkName)
            .WithEnvironment("WorkflowLauncher__RabbitMqHost", RabbitMqAlias)
            .WithEnvironment("WorkflowLauncher__RabbitMqPort", "5672")
            .WithEnvironment("WorkflowLauncher__RabbitMqUserName", "guest")
            .WithEnvironment("WorkflowLauncher__RabbitMqPassword", "guest")
            .WithEnvironment("WorkflowDispatcher__RunOutputDirectory", ContainerRunOutput)
            .WithEnvironment("WorkflowDispatcher__RunOutputHostDirectory", _runOutputDir)
            .WithEnvironment("WorkflowDispatcher__WorkspaceRootDirectory", ContainerWorkspaces)
            .WithEnvironment("WorkflowDispatcher__WorkspaceRootHostDirectory", _workspaceDir)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("WorkflowDispatcher started"))
            .Build();
        await _runner.StartAsync();

        _coreApi = new ContainerBuilder(CoreApiImageName)
            .WithNetwork(_network)
            .WithNetworkAliases(CoreApiAlias)
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithEnvironment("RabbitMq__Host", RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port", "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("PlatformData__Backend", "InMemory")
            .WithEnvironment("PlatformData__ProtectionKeyBase64", Convert.ToBase64String(new byte[32]))
            .WithEnvironment("CoreSecurity__BootstrapApiKey", BootstrapApiKey)
            .WithEnvironment("CoreApi__AllowDispatchWithoutRunner", "true")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health")))
            .Build();
        await _coreApi.StartAsync();

        MessageBusClient = await RabbitMqClient.CreateAsync(
            _rabbitMq.Hostname, _rabbitMq.GetMappedPublicPort(5672));
        CoreApiClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{_coreApi.GetMappedPublicPort(8080)}")
        };
        CoreApiClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BootstrapApiKey);

        await RegisterScenarioAsync(simStackDigestRef, rabbitDigestRef);
    }

    /// <summary>
    /// The worked-example topology, hand-built with the freshly minted digests, registered
    /// over the normal governed path: register (docker:// = Pending) → approve → Active,
    /// plus the runtime-spawnable base in the environment-base catalog.
    /// </summary>
    private static async Task RegisterScenarioAsync(string simStackDigestRef, string rabbitDigestRef)
    {
        const string busUri = "amqp://sim:sim-pass@rabbit:5672/";
        var schema = new WorkflowSchema(WorkflowType, [], [])
        {
            Version = "1.0.0",
            Companions =
            [
                new CompanionDeclaration("rabbit", rabbitDigestRef)
                {
                    EnvironmentVariables =
                    [
                        new CompanionEnvironmentVariable("RABBITMQ_DEFAULT_USER", CompanionEnvironmentVariable.Literal) { Value = "sim" },
                        new CompanionEnvironmentVariable("RABBITMQ_DEFAULT_PASS", CompanionEnvironmentVariable.Literal) { Value = "sim-pass" }
                    ],
                    Readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, 5672) { TimeoutSeconds = 120 }
                },
                new CompanionDeclaration("machine", simStackDigestRef)
                {
                    MinInstances = 0, MaxInstances = 8, CountInput = "machines",
                    EnvironmentVariables =
                    [
                        new CompanionEnvironmentVariable("ROLE", CompanionEnvironmentVariable.Literal) { Value = "machine" }
                    ],
                    Readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, 9000) { TimeoutSeconds = 90 },
                    PodVolumes = [new CompanionPodVolume("logs", "/var/log/app")]
                },
                new CompanionDeclaration("fleet-manager", simStackDigestRef)
                {
                    StartAfter = ["rabbit", "machine"],
                    EnvironmentVariables =
                    [
                        new CompanionEnvironmentVariable("ROLE", CompanionEnvironmentVariable.Literal) { Value = "fleet-manager" },
                        new CompanionEnvironmentVariable("BUS_URI", CompanionEnvironmentVariable.Literal) { Value = busUri },
                        new CompanionEnvironmentVariable("MACHINES", CompanionEnvironmentVariable.InstanceEndpoints)
                        {
                            SourceCompanion = "machine", Port = 9000
                        }
                    ],
                    Readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, 8080) { TimeoutSeconds = 90 },
                    PodVolumes = [new CompanionPodVolume("logs", "/var/log/app")]
                },
                new CompanionDeclaration("coordinator", simStackDigestRef)
                {
                    StartAfter = ["rabbit"],
                    EnvironmentVariables =
                    [
                        new CompanionEnvironmentVariable("ROLE", CompanionEnvironmentVariable.Literal) { Value = "coordinator" },
                        new CompanionEnvironmentVariable("BUS_URI", CompanionEnvironmentVariable.Literal) { Value = busUri }
                    ],
                    Readiness = new CompanionReadinessProbe(CompanionReadinessProbe.Tcp, 7000) { TimeoutSeconds = 90 },
                    PodVolumes = [new CompanionPodVolume("logs", "/var/log/app")]
                }
            ],
            // EXACTLY the scenario's runtime spawns: with 4+ declared companions live, the
            // run only succeeds because the envelope caps runtime spawns alone.
            PodControl = new PodControlDeclaration(2, "Simulated machines the test case spawns")
            {
                PodVolumes = ["logs"]
            }
        };

        (await CoreApiClient.PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest(WorkflowType,
                PackageUri: "docker://" + DummyWorkflowsImageName,
                SchemaJson: JsonSerializer.Serialize(schema)))).EnsureSuccessStatusCode();
        (await CoreApiClient.PostAsync($"/api/workflow-types/{WorkflowType}/approve", null))
            .EnsureSuccessStatusCode();

        // The re-adoption workflow: no declared companions, one-slot runtime envelope. Its
        // pod network/volumes still materialize at launch, so a post-restart spawn has a
        // home — the restart test's whole point.
        var restartSchema = new WorkflowSchema(RestartWorkflowType, [], [])
        {
            Version = "1.0.0",
            PodControl = new PodControlDeclaration(1, "One machine, spawned only after the runner restarted")
        };
        (await CoreApiClient.PostAsJsonAsync("/api/workflow-types",
            new RegisterWorkflowTypeRequest(RestartWorkflowType,
                PackageUri: "docker://" + DummyWorkflowsImageName,
                SchemaJson: JsonSerializer.Serialize(restartSchema)))).EnsureSuccessStatusCode();
        (await CoreApiClient.PostAsync($"/api/workflow-types/{RestartWorkflowType}/approve", null))
            .EnsureSuccessStatusCode();

        (await CoreApiClient.PostAsJsonAsync("/api/environment-bases",
            new UpsertEnvironmentBase(SpawnableBaseName, "1",
                "Simulated hardware machine base", ImageReference: simStackDigestRef)))
            .EnsureSuccessStatusCode();
    }

    /// <summary>Tags + pushes an image through the local registry and returns its digest reference.</summary>
    private static async Task<string> MintDigestAsync(string sourceImage, ushort registryPort, string repoName)
    {
        var target = $"localhost:{registryPort}/{repoName}:st";
        await RunDockerAsync($"tag {sourceImage} {target}");
        string pushOutput = "";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                pushOutput = await RunDockerAsync($"push {target}");
                break;
            }
            catch (InvalidOperationException) when (attempt < 5)
            {
                await Task.Delay(2000); // the registry may still be coming up
            }
        }
        var digest = Regex.Match(pushOutput, "digest: (sha256:[0-9a-f]{64})").Groups[1].Value;
        if (digest.Length == 0)
            throw new InvalidOperationException($"could not parse the pushed digest from:\n{pushOutput}");
        return $"localhost:{registryPort}/{repoName}@{digest}";
    }

    private static async Task<string> RunDockerAsync(string arguments)
    {
        var psi = new ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"docker {arguments} failed:\n{stdout}\n{stderr}");
        return stdout + stderr;
    }

    internal static async Task<string> RunnerLogTailAsync(int maxChars = 4000)
    {
        var (stdout, stderr) = await Runner.GetLogsAsync();
        var all = stdout + "\n" + stderr;
        return all.Length <= maxChars ? all : all[^maxChars..];
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        CoreApiClient?.Dispose();
        if (MessageBusClient is IAsyncDisposable disposable) await disposable.DisposeAsync();
        if (_coreApi is not null) await _coreApi.DisposeAsync();
        if (_runner is not null) await _runner.DisposeAsync();
        if (_registryName is not null)
            try { await RunDockerAsync($"rm -f {_registryName}"); } catch (InvalidOperationException) { }
        if (_rabbitMq is not null) await _rabbitMq.DisposeAsync();
        if (_network is not null) await _network.DisposeAsync();
        foreach (var dir in new[] { _runOutputDir, _workspaceDir, ArtifactsDir })
            try { if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* leave for manual cleanup */ }
    }
}
