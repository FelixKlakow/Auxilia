using System.Diagnostics;
using System.Net.Http.Headers;
using Auxilia.Messaging;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.CoreApiDispatch;

/// <summary>
/// System environment for the Core API dispatch acceptance tests.
///
/// Topology:
/// <list type="bullet">
///   <item>RabbitMQ — message broker (alias <c>rabbitmq</c>).</item>
///   <item>Runner — the runner; Docker socket bind-mounted so it launches workflow
///         containers via the Docker API (<c>DockerWorkflowLauncher</c>).</item>
///   <item>Core.Api — the control plane; its own in-memory database, a bootstrap Administrator
///         API key, and one seeded static configuration. Drives the runner over the bus.</item>
///   <item>Dummy-workflows image — pre-built; launched on demand when a run reaches the runner.</item>
/// </list>
///
/// The Core never shares a database with the runner: it resolves configurations locally and
/// hands the runner a self-contained RunWorkflowCommand.
/// </summary>
[SetUpFixture]
public class CoreApiDispatchEnvironment
{
    internal const string DummyWorkflowsImageName = "auxilia-dummy-workflows:system-test";
    internal const string RunnerImageName        = "auxilia-core-runner:system-test";
    internal const string CoreApiImageName         = "auxilia-core-api:system-test";
    internal const string DummyPackageUri          = "docker://auxilia-dummy-workflows:system-test";
    internal const string DummyWorkflowType        = "simple-git-commit-workflow";
    internal const string StaticConfigurationName  = "static-dummy";
    internal const string BootstrapApiKey          = "aux-system-test-key-abcdef0123456789";
    // Credentialed-slot test: the dummy workflow that resolves a connector-backed slot through
    // the Core, and the provider type its slot handler is registered under.
    internal const string CredentialWorkflowType   = "credential-resolution-workflow";
    internal const string CredentialProviderType   = "credential-probe";
    // Per-run repository test: an authenticated git server, and the no-slot workflow that verifies
    // the runner cloned + mounted the repo (using the JIT-resolved connector credential).
    internal const string GitServerImageName       = "auxilia-git-server:system-test";
    internal const string GitServerAlias           = "gitserver";
    internal const string RepositoryWorkflowType   = "workspace-repository-workflow";
    internal const string RepositoryCloneUrl       = "http://gitserver/git/test.git";
    internal const string GitUsername              = "builduser";
    internal const string GitPassword              = "the-pat";

    private const string RabbitMqAlias = "rabbitmq";
    private const string RabbitMqImage = "rabbitmq:3.13-management";
    private const string CoreApiAlias = "core-api";
    // Shared run-output + workspace roots: the runner writes to its container view; the Docker
    // daemon (and thus workflow containers) bind-mount the same physical directory by its host path.
    private const string ContainerRunOutput  = "/run-output";
    private const string ContainerWorkspaces = "/workspaces";

    private static readonly string NetworkName =
        $"auxilia-coreapi-{Guid.NewGuid():N}".Substring(0, 30);

    private static readonly string RepoRoot = RepoPaths.Root;

    private IContainer _runner = null!;
    private IContainer _coreApi = null!;
    private IContainer _gitServer = null!;
    private string _runOutputDir = null!;
    private string _workspaceDir = null!;
    private INetwork _network = null!;
    private RabbitMqContainer _rabbitMq = null!;

    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    public static HttpClient CoreApiClient { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await Task.WhenAll(
            BuildImageAsync(RunnerImageName, "Source/Platform/Auxilia.Core.Runner/Dockerfile"),
            BuildImageAsync(DummyWorkflowsImageName, "Tests/System/Auxilia.Workflows.Testing/Dockerfile"),
            BuildImageAsync(CoreApiImageName, "Source/Platform/Auxilia.Core.Api/Dockerfile"),
            BuildImageAsync(GitServerImageName, "Tests/System/Auxilia.SystemTestSuite/GitServer/Dockerfile"));

        _network = new NetworkBuilder().WithName(NetworkName).Build();
        await _network.CreateAsync();

        // Authenticated git server the runner clones per-run repositories from.
        _gitServer = new ContainerBuilder(GitServerImageName)
            .WithNetwork(_network).WithNetworkAliases(GitServerAlias)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("resuming normal operations"))
            .Build();
        await _gitServer.StartAsync();

        _rabbitMq = new RabbitMqBuilder(RabbitMqImage)
            .WithUsername("guest").WithPassword("guest")
            .WithNetwork(_network).WithNetworkAliases(RabbitMqAlias)
            .Build();
        await _rabbitMq.StartAsync();

        _runOutputDir = Path.Combine(Path.GetTempPath(), $"auxilia-coreapi-output-{Guid.NewGuid():N}");
        _workspaceDir = Path.Combine(Path.GetTempPath(), $"auxilia-coreapi-workspaces-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runOutputDir);
        Directory.CreateDirectory(_workspaceDir);

        const string dockerSocket = "/var/run/docker.sock";
        _runner = new ContainerBuilder(RunnerImageName)
            .WithNetwork(_network)
            .WithBindMount(dockerSocket, dockerSocket)
            // Shared run-output + workspace roots so per-run repository clones are visible to the
            // workflow container the daemon launches (ARCHITECTURE §9 — Docker-in-Docker bind split).
            .WithBindMount(_runOutputDir, ContainerRunOutput)
            .WithBindMount(_workspaceDir, ContainerWorkspaces)
            .WithEnvironment("RabbitMq__Host", RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port", "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            // Credentialed slots resolve just-in-time from the Core over HTTP — the runner holds
            // no secrets. Reaches Core.Api by its network alias (see the Core.Api container below).
            .WithEnvironment("WorkflowDispatcher__CoreApiBaseAddress", $"http://{CoreApiAlias}:8080")
            .WithEnvironment("WorkflowLauncher__NetworkName", NetworkName)
            .WithEnvironment("WorkflowLauncher__RabbitMqHost", RabbitMqAlias)
            .WithEnvironment("WorkflowLauncher__RabbitMqPort", "5672")
            .WithEnvironment("WorkflowLauncher__RabbitMqUserName", "guest")
            .WithEnvironment("WorkflowLauncher__RabbitMqPassword", "guest")
            // The credential-probe slot handler is baked onto the runner image at /app/slots.
            // Its DLL is copied into the workflow container at launch; the test-fake plugin is
            // unsigned, so it loads only when the workflow container runs in developer mode.
            .WithEnvironment(
                $"WorkflowLauncher__SlotPackages__{CredentialProviderType}",
                "/app/slots/Auxilia.Slots.CredentialProbe.slothandler.dll")
            .WithEnvironment(
                "WorkflowLauncher__ExtraEnvironmentVariables__AUXILIA_DEVELOPER_MODE", "1")
            // The daemon's view of the shared roots (host paths) that it bind-mounts into workflows.
            .WithEnvironment("WorkflowDispatcher__RunOutputDirectory",         ContainerRunOutput)
            .WithEnvironment("WorkflowDispatcher__RunOutputHostDirectory",     _runOutputDir)
            .WithEnvironment("WorkflowDispatcher__WorkspaceRootDirectory",     ContainerWorkspaces)
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
            .WithEnvironment("CoreApi__StaticConfigurations__0__Name", StaticConfigurationName)
            .WithEnvironment("CoreApi__StaticConfigurations__0__WorkflowType", DummyWorkflowType)
            .WithEnvironment("CoreApi__StaticConfigurations__0__Context__WORKFLOW_NAME", DummyWorkflowType)
            // Statically registered workflow types (Active) — the operator trust decision for the
            // suite: every type the dispatch tests run, all backed by the dummy-workflows image.
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__WorkflowType", DummyWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__PackageUri", DummyPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__1__WorkflowType", CredentialWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__1__PackageUri", DummyPackageUri)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__2__WorkflowType", RepositoryWorkflowType)
            .WithEnvironment("CoreApi__StaticWorkflowTypes__2__PackageUri", DummyPackageUri)
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
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        CoreApiClient?.Dispose();
        if (MessageBusClient is IAsyncDisposable disposable) await disposable.DisposeAsync();
        if (_coreApi is not null) await _coreApi.DisposeAsync();
        if (_gitServer is not null) await _gitServer.DisposeAsync();
        if (_runner is not null) await _runner.DisposeAsync();
        if (_rabbitMq is not null) await _rabbitMq.DisposeAsync();
        if (_network is not null) await _network.DisposeAsync();
        foreach (var dir in new[] { _runOutputDir, _workspaceDir })
            try { if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* leave for manual cleanup */ }
    }

    internal static async Task BuildImageAsync(string tag, string dockerfilePath)
    {
        if (Environment.GetEnvironmentVariable("AUXILIA_PREBUILT_IMAGES") == "1" ||
            File.Exists(Path.Combine(RepoRoot, ".prebuilt-images")))
        {
            await Console.Out.WriteLineAsync($"Prebuilt-images opt-out active — skipping docker build for {tag}.");
            return;
        }

        try
        {
            await BuildImageOnceAsync(tag, dockerfilePath, TimeSpan.FromMinutes(8));
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync(
                $"docker build for {tag} timed out — retrying once (daemon may have been wedged).");
            await BuildImageOnceAsync(tag, dockerfilePath, TimeSpan.FromMinutes(8));
        }
    }

    private static async Task BuildImageOnceAsync(string tag, string dockerfilePath, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("docker", $"build -t {tag} -f {dockerfilePath} .")
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start docker build.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"docker build for {tag} exceeded {timeout.TotalMinutes:0} minutes.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"docker build failed for {tag} (exit {process.ExitCode}):\n{stdout}\n{stderr}");
    }
}
