using System.Security.Cryptography;
using Auxilia.Core.Client;
using Auxilia.Messaging;
using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using DotNet.Testcontainers.Volumes;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>
/// System environment for the <c>Auxilia.Core.Client</c> surface tests — the ONLY suite that
/// drives the Core exclusively through <see cref="ICoreClient"/> over a real network (every
/// other fixture uses a raw HttpClient). Topology: RabbitMQ + Core.Runner (Docker socket, so
/// dummy workflows really run) + Core.Api with a JSON-file store so state SURVIVES a container
/// restart — <see cref="RestartCoreApiAsync"/> is the mid-stream reconnect scenario. The Core's
/// SSE keepalive is shortened so client idle-timeout detection is exercised in test time.
/// </summary>
[SetUpFixture]
public class CoreClientEnvironment
{
    internal const string BootstrapApiKey = "aux-client-surface-key-abcdef0123456789";
    internal const string SleepingWorkflowType = "sleeping-workflow";
    internal const string EchoWorkflowType = "echo-workflow";
    internal const string EchoDecisionWorkflowType = "echo-decision-workflow";
    internal const string ContainmentProbeWorkflowType = "containment-probe-workflow";
    internal const string CredentialRefreshWorkflowType = "credential-refresh-workflow";
    internal const string ViewSequenceWorkflowType = "view-sequence-workflow";
    internal const string IdleSessionWorkflowType = "idle-session-workflow";
    internal const string InputFileWorkflowType = "input-file-workflow";
    internal const string CheckpointWorkflowType = "checkpoint-workflow";

    /// <summary>The runner's cap on how long a run may wait for its operator — short, so the cap itself is testable.</summary>
    internal static readonly TimeSpan MaxIdle = TimeSpan.FromSeconds(15);
    /// <summary>The test-fake slot provider whose settings a workflow can read back (baked onto the runner image).</summary>
    internal const string CredentialProbeProviderType = "credential-probe";
    /// <summary>The runner's per-container memory ceiling here (small enough to reach on purpose).</summary>
    internal const int ContainerMemoryMb = 512;
    /// <summary>The dummy image by its content (images are approved by digest) — set once it is built.</summary>
    internal static string DummyPackageUri { get; private set; } = "";
    /// <summary>The Core's SSE keepalive cadence — client idle timeouts must exceed this.</summary>
    internal const int KeepaliveSeconds = 5;

    private const string RabbitMqAlias = "rabbitmq";
    private const string CoreApiAlias = "core-api";
    /// <summary>The Core's address ON the test network — what the runner downloads core:// packages from.</summary>
    internal const string CoreApiInternalBaseAddress = $"http://{CoreApiAlias}:8080";

    private static readonly string NetworkName =
        $"auxilia-client-{Guid.NewGuid():N}".Substring(0, 30);

    private static INetwork _network = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private static IContainer _runner = null!;
    /// <summary>The Docker API guard: it alone holds the daemon socket, the runner talks to it.</summary>
    private static IContainer _dockerGuard = null!;
    internal const string DockerGuardImageName = "auxilia-docker-guard:system-test";
    /// <summary>The guard as the test host reaches it (a Docker endpoint URI).</summary>
    internal static string DockerGuardEndpoint { get; private set; } = "";
    /// <summary>
    /// The runner's run roots (packages, run output, workspaces — its temp directory): a volume
    /// mounted into the runner at the very path the DAEMON sees it under, so every directory the
    /// runner prepares is the one its containers bind. The guard's only bind root.
    /// </summary>
    private static IVolume _runRoots = null!;
    internal static string RunRootsPath { get; private set; } = "";
    /// <summary>The artifact payload store the runner writes and the Core serves (a deployment shares it).</summary>
    private static IVolume _artifacts = null!;
    private const string ArtifactPayloadRoot = "/var/lib/auxilia/artifacts";
    private static IContainer _coreApi = null!;
    private static readonly List<HttpClient> HttpClients = [];

    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    public static string CoreBaseAddress { get; private set; } = null!;

    /// <summary>The test network — signing fixtures attach their package server to it.</summary>
    internal static INetwork Network => _network;

    /// <summary>Runs a command inside the runner container — what a compromised runner process could do.</summary>
    internal static async Task<string> ExecInRunnerAsync(IList<string> command, CancellationToken ct)
    {
        var result = await _runner.ExecAsync(command, ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"'{string.Join(' ', command)}' failed in the runner: {result.Stderr}");
        return result.Stdout;
    }

    /// <summary>
    /// The runner's combined log so far — the observable for runner-INTERNAL steps that have no
    /// client-visible surface (e.g. the package-extracted line between signature verification
    /// and container launch in <c>SigningRoundtripSystemTests</c>).
    /// </summary>
    internal static async Task<string> GetRunnerLogAsync(CancellationToken ct = default)
    {
        var (stdout, stderr) = await _runner.GetLogsAsync(ct: ct);
        return stdout + '\n' + stderr;
    }

    /// <summary>
    /// A publisher keypair the Core is configured to TRUST (CoreApi:TrustedPublisherKeys):
    /// packages signed with it activate immediately. Owned by the environment, per-session.
    /// </summary>
    internal static RSA TrustedPublisherRsa { get; private set; } = null!;
    internal static string TrustedPublisherKeyBase64 { get; private set; } = null!;

    /// <summary>The platform signing key (CoreApi:SigningKeyPemFile) — approvals re-sign with it.</summary>
    internal static string PlatformPublicKeyBase64 { get; private set; } = null!;

    /// <summary>
    /// The run context every dummy-image dispatch needs: the image hosts several workflows and
    /// selects by the WORKFLOW_NAME context value.
    /// </summary>
    public static Dictionary<string, string> ContextFor(string workflowType, params (string Key, string Value)[] extra)
    {
        var context = new Dictionary<string, string> { ["WORKFLOW_NAME"] = workflowType };
        foreach (var (key, value) in extra)
            context[key] = value;
        return context;
    }

    /// <summary>A fresh typed client authenticated with <paramref name="apiKey"/> (bootstrap admin by default).</summary>
    public static ICoreClient CreateClient(string? apiKey = null, Action<CoreClientOptions>? configure = null)
    {
        var options = new CoreClientOptions
        {
            BaseAddress = CoreBaseAddress,
            ApiKey = apiKey ?? BootstrapApiKey,
            // Faster reconnects than the production defaults so restart tests stay snappy;
            // idle timeout must exceed the Core's keepalive cadence.
            StreamReconnectInitialBackoffSeconds = 1,
            StreamReconnectMaxBackoffSeconds = 2,
            StreamIdleTimeoutSeconds = KeepaliveSeconds * 3
        };
        configure?.Invoke(options);
        var http = new HttpClient { BaseAddress = new Uri(CoreBaseAddress) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
        HttpClients.Add(http);
        return new CoreClient(http, options);
    }

    /// <summary>
    /// Stops and starts the Core.Api CONTAINER (state survives in its JSON store, the mapped
    /// host port is retained) — the real-outage scenario the resilient client streams exist for.
    /// </summary>
    public static async Task RestartCoreApiAsync()
    {
        await _coreApi.StopAsync();
        await _coreApi.StartAsync();
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Signing material for the package-trust roundtrip (SigningRoundtripSystemTests): a
        // trusted publisher key handed to the Core as configuration, and a platform signing
        // key whose PEM is mapped into the Core.Api container.
        TrustedPublisherRsa = RSA.Create(2048);
        TrustedPublisherKeyBase64 = Convert.ToBase64String(TrustedPublisherRsa.ExportSubjectPublicKeyInfo());
        string platformSigningPem;
        using (var platformKey = RSA.Create(2048))
        {
            platformSigningPem = platformKey.ExportRSAPrivateKeyPem();
            PlatformPublicKeyBase64 = Convert.ToBase64String(platformKey.ExportSubjectPublicKeyInfo());
        }

        await TestImages.BuildEgressGatewayAsync();
        await Task.WhenAll(
            TestImages.BuildImageAsync(CoreApiDispatch.CoreApiDispatchEnvironment.RunnerImageName,
                "Source/Platform/Auxilia.Core.Runner/Dockerfile"),
            TestImages.BuildImageAsync(CoreApiDispatch.CoreApiDispatchEnvironment.DummyWorkflowsImageName,
                "Tests/System/Auxilia.Workflows.Testing/Dockerfile"),
            TestImages.BuildImageAsync(CoreApiDispatch.CoreApiDispatchEnvironment.CoreApiImageName,
                "Source/Platform/Auxilia.Core.Api/Dockerfile"),
            TestImages.BuildImageAsync(DockerGuardImageName, "Source/Platform/Auxilia.DockerGuard/Dockerfile"));
        DummyPackageUri = await TestImages.PinnedCoordinateAsync(
            CoreApiDispatch.CoreApiDispatchEnvironment.DummyWorkflowsImageName);

        _network = new NetworkBuilder().WithName(NetworkName).Build();
        await _network.CreateAsync();

        _rabbitMq = new RabbitMqBuilder("rabbitmq:4-management")
            .WithUsername("guest").WithPassword("guest")
            .WithNetwork(_network).WithNetworkAliases(RabbitMqAlias)
            .Build();
        await _rabbitMq.StartAsync();

        _runRoots = new VolumeBuilder()
            .WithName($"auxilia-run-roots-{TestSession.Id}")
            .WithLabel(TestSession.LabelKey, TestSession.Id)
            .Build();
        await _runRoots.CreateAsync();
        using (var daemon = new DockerClientConfiguration().CreateClient())
            RunRootsPath = (await daemon.Volumes.InspectAsync(_runRoots.Name)).Mountpoint;
        _artifacts = new VolumeBuilder()
            .WithName($"auxilia-artifacts-{TestSession.Id}")
            .WithLabel(TestSession.LabelKey, TestSession.Id)
            .Build();
        await _artifacts.CreateAsync();

        // The production shape: the runner never holds the daemon socket — the Docker API guard
        // does, and allows exactly what a runner does. Every run in this environment (the
        // containment probe included) goes through it.
        const string dockerSocket = "/var/run/docker.sock";
        _dockerGuard = new ContainerBuilder(DockerGuardImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("docker-guard")
            .WithBindMount(dockerSocket, dockerSocket)
            .WithEnvironment("DockerGuard__OutboundNetworks__0", NetworkName)
            .WithEnvironment("DockerGuard__OutboundNetworks__1", "bridge")
            // Every run directory lives below the run roots; the guard carries their binds out
            // beneath them (its root volume carries the session label the teardown sweeps).
            .WithEnvironment("DockerGuard__BindRoots__0", RunRootsPath)
            .WithEnvironment($"DockerGuard__RootVolumeLabels__{TestSession.LabelKey}", TestSession.Id)
            .WithPortBinding(2375, assignRandomHostPort: true)
            // Deployed as recommended: read-only, every capability dropped, scratch on tmpfs.
            .WithTmpfsMount("/tmp")
            .WithCreateParameterModifier(create =>
            {
                create.HostConfig.ReadonlyRootfs = true;
                create.HostConfig.CapDrop = ["ALL"];
                create.HostConfig.SecurityOpt = ["no-new-privileges:true"];
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(2375))
            .Build();
        await _dockerGuard.StartAsync();
        DockerGuardEndpoint = $"tcp://localhost:{_dockerGuard.GetMappedPublicPort(2375)}";

        _runner = new ContainerBuilder(CoreApiDispatch.CoreApiDispatchEnvironment.RunnerImageName)
            .WithNetwork(_network)
            .WithEnvironment("WorkflowLauncher__DockerSocketPath", "tcp://docker-guard:2375")
            // Packages, run output, workspaces and the warm cache all default below the temp
            // directory — here the run roots, at the daemon's own path for them.
            .WithVolumeMount(_runRoots, RunRootsPath)
            .WithEnvironment("TMPDIR", RunRootsPath)
            .WithVolumeMount(_artifacts, ArtifactPayloadRoot)
            .WithEnvironment("ArtifactStore__PayloadRoot", ArtifactPayloadRoot)
            .WithEnvironment("RabbitMq__Host", RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port", "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("WorkflowDispatcher__CoreApiBaseAddress", $"http://{CoreApiAlias}:8080")
            .WithEnvironment("WorkflowLauncher__NetworkName", NetworkName)
            // Zip-package launches bind-mount into this image; the dummy image is already built
            // locally, so a launch never stalls on a registry pull (SigningRoundtripSystemTests).
            .WithEnvironment("WorkflowLauncher__RuntimeImage",
                CoreApiDispatch.CoreApiDispatchEnvironment.DummyWorkflowsImageName)
            .WithEnvironment("WorkflowLauncher__RabbitMqHost", RabbitMqAlias)
            .WithEnvironment("WorkflowLauncher__RabbitMqPort", "5672")
            .WithEnvironment("WorkflowLauncher__EgressGatewayImage", TestImages.EgressGatewayImageName)
            .WithEnvironment("WorkflowLauncher__ContainerMemoryMb", ContainerMemoryMb.ToString())
            .WithEnvironment("WorkflowLauncher__MaxIdleMinutes",
                MaxIdle.TotalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture))
            // The credential-probe slot (baked onto the runner image) for the personal-credential
            // runs; the test-fake plugin is unsigned, so workflow containers run in developer
            // mode, which relaxes plugin signature checks and nothing else.
            .WithEnvironment($"WorkflowLauncher__SlotPackages__{CredentialProbeProviderType}",
                "/app/slots/Auxilia.Slots.CredentialProbe.slothandler.dll")
            .WithEnvironment("WorkflowLauncher__ExtraEnvironmentVariables__AUXILIA_DEVELOPER_MODE", "1")
            // Everything this runner creates carries the session label the teardown sweeps.
            .WithEnvironment(TestSession.RunnerVariable, TestSession.Id)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("WorkflowDispatcher started"))
            .Build();
        await _runner.StartAsync();

        _coreApi = new ContainerBuilder(CoreApiDispatch.CoreApiDispatchEnvironment.CoreApiImageName)
            .WithNetwork(_network)
            .WithNetworkAliases(CoreApiAlias)
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithEnvironment("RabbitMq__Host", RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port", "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            // JSON-file store INSIDE the container: stop/start (not recreate) keeps the
            // filesystem, so runs/principals/registrations survive RestartCoreApiAsync.
            .WithEnvironment("PlatformData__Backend", "Json")
            .WithEnvironment("PlatformData__JsonDirectory", "/core-data")
            .WithVolumeMount(_artifacts, ArtifactPayloadRoot)
            .WithEnvironment("ArtifactStore__PayloadRoot", ArtifactPayloadRoot)
            .WithEnvironment("PlatformData__ProtectionKeyBase64", Convert.ToBase64String(new byte[32]))
            .WithEnvironment("CoreSecurity__BootstrapApiKey", BootstrapApiKey)
            // Dispatch queues on the bus instead of gating on a seen runner heartbeat — the
            // runner IS live here, its first heartbeat just lags the first test's dispatch.
            .WithEnvironment("CoreApi__AllowDispatchWithoutRunner", "true")
            .WithEnvironment("CoreApi__SseKeepaliveSeconds", KeepaliveSeconds.ToString())
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__WorkflowType", SleepingWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__PackageUri", DummyPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__1__WorkflowType", EchoWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__1__PackageUri", DummyPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__2__WorkflowType", EchoDecisionWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__2__PackageUri", DummyPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__3__WorkflowType", ContainmentProbeWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__3__PackageUri", DummyPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__4__WorkflowType", CredentialRefreshWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__4__PackageUri", DummyPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__5__WorkflowType", ViewSequenceWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__5__PackageUri", DummyPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__6__WorkflowType", IdleSessionWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__6__PackageUri", DummyPackageUri)
            // Registered WITH its schema, as a registration normally is: the Core knows the
            // declared input files before the type's first run binds one.
            .WithEnvironment("CoreApi__StaticWorkflowTypes__7__WorkflowType", InputFileWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__7__PackageUri", DummyPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__7__SchemaJson",
                System.Text.Json.JsonSerializer.Serialize(
                    Auxilia.Workflows.Testing.DummyWorkflows.InputFileWorkflow.Schema()))
            // Its checkpoints are known to the Core before the first save arrives.
            .WithEnvironment("CoreApi__StaticWorkflowTypes__8__WorkflowType", CheckpointWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__8__PackageUri", DummyPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__8__SchemaJson",
                System.Text.Json.JsonSerializer.Serialize(
                    Auxilia.Workflows.Testing.DummyWorkflows.CheckpointWorkflow.Schema()))
            // The package-trust roundtrip: a trusted publisher key (auto-Active uploads), the
            // platform signing key (approve-time re-sign), and the network-internal base address
            // core:// package URIs resolve to — the RUNNER must be able to reach it.
            .WithEnvironment("CoreApi__TrustedPublisherKeys__0", TrustedPublisherKeyBase64)
            .WithEnvironment("CoreApi__SigningKeyPemFile", "/core-keys/platform-signing.pem")
            .WithResourceMapping(
                System.Text.Encoding.UTF8.GetBytes(platformSigningPem), "/core-keys/platform-signing.pem")
            .WithEnvironment("CoreApi__PublicBaseAddress", CoreApiInternalBaseAddress)
            // An EXPLICIT free host port, not a random published one: Docker reassigns random
            // ports on container restart, and RestartCoreApiAsync must come back on the SAME
            // address for the client's internal reconnect to find it.
            .WithPortBinding(FreeTcpPort(), 8080)
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health")))
            .Build();
        await _coreApi.StartAsync();

        CoreBaseAddress = $"http://localhost:{_coreApi.GetMappedPublicPort(8080)}";
        MessageBusClient = await RabbitMqClient.CreateAsync(
            _rabbitMq.Hostname, _rabbitMq.GetMappedPublicPort(5672));
    }

    private static async Task DumpLogsAsync(IContainer? container, string name)
    {
        if (container is null)
            return;
        try
        {
            var (stdout, stderr) = await container.GetLogsAsync(timestampsEnabled: true);
            var path = Path.Combine(Path.GetTempPath(), $"auxilia-client-surface-{name}.log");
            await File.WriteAllTextAsync(path, stdout + '\n' + stderr);
            await Console.Out.WriteLineAsync($"{name} log written to {path}");
        }
        catch (Exception ex)
        {
            await Console.Out.WriteLineAsync($"({name} logs unavailable: {ex.Message})");
        }
    }

    private static int FreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        // Session objects first: a leftover gateway would pin the test network.
        await TestSession.SweepAsync();
        // Container logs are the only evidence when the platform misbehaves mid-suite; the
        // test process's console is swallowed by vstest, so persist them next to the repo.
        await DumpLogsAsync(_coreApi, "core-api");
        await DumpLogsAsync(_runner, "core-runner");
        await DumpLogsAsync(_dockerGuard, "docker-guard");

        foreach (var http in HttpClients) http.Dispose();
        HttpClients.Clear();
        TrustedPublisherRsa?.Dispose();
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();
        if (_coreApi is not null) await _coreApi.DisposeAsync();
        if (_runner is not null) await _runner.DisposeAsync();
        if (_dockerGuard is not null) await _dockerGuard.DisposeAsync();
        await TestSession.SweepAsync();
        if (_runRoots is not null) await _runRoots.DisposeAsync();
        if (_artifacts is not null) await _artifacts.DisposeAsync();
        if (_rabbitMq is not null) await _rabbitMq.DisposeAsync();
        if (_network is not null) await _network.DisposeAsync();
        // A run whose containment the stopped runner never released leaves nothing behind.
        await TestSession.SweepAsync();
    }
}
