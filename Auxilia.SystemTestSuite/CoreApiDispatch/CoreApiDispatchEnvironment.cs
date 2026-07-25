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
///   <item>SteeringInstance — the runner; Docker socket bind-mounted so it launches workflow
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
    internal const string SteeringImageName        = "auxilia-core-runner:system-test";
    internal const string CoreApiImageName         = "auxilia-core-api:system-test";
    internal const string DummyPackageUri          = "docker://auxilia-dummy-workflows:system-test";
    internal const string DummyWorkflowType        = "simple-git-commit-workflow";
    internal const string StaticConfigurationName  = "static-dummy";
    internal const string BootstrapApiKey          = "aux-system-test-key-abcdef0123456789";

    private const string RabbitMqAlias = "rabbitmq";
    private const string RabbitMqImage = "rabbitmq:3.13-management";
    private const string CoreApiAlias = "core-api";

    private static readonly string NetworkName =
        $"auxilia-coreapi-{Guid.NewGuid():N}".Substring(0, 30);

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private IContainer _steeringInstance = null!;
    private IContainer _coreApi = null!;
    private INetwork _network = null!;
    private RabbitMqContainer _rabbitMq = null!;

    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    public static HttpClient CoreApiClient { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await Task.WhenAll(
            BuildImageAsync(SteeringImageName, "Source/Auxilia.Core.Runner/Dockerfile"),
            BuildImageAsync(DummyWorkflowsImageName, "Auxilia.Workflows.Testing/Dockerfile"),
            BuildImageAsync(CoreApiImageName, "Source/Auxilia.Core.Api/Dockerfile"));

        _network = new NetworkBuilder().WithName(NetworkName).Build();
        await _network.CreateAsync();

        _rabbitMq = new RabbitMqBuilder(RabbitMqImage)
            .WithUsername("guest").WithPassword("guest")
            .WithNetwork(_network).WithNetworkAliases(RabbitMqAlias)
            .Build();
        await _rabbitMq.StartAsync();

        const string dockerSocket = "/var/run/docker.sock";
        _steeringInstance = new ContainerBuilder(SteeringImageName)
            .WithNetwork(_network)
            .WithBindMount(dockerSocket, dockerSocket)
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
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("WorkflowDispatcher started"))
            .Build();
        await _steeringInstance.StartAsync();

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
            .WithEnvironment("CoreApi__StaticConfigurations__0__Name", StaticConfigurationName)
            .WithEnvironment("CoreApi__StaticConfigurations__0__WorkflowType", DummyWorkflowType)
            .WithEnvironment("CoreApi__StaticConfigurations__0__PackageUri", DummyPackageUri)
            .WithEnvironment("CoreApi__StaticConfigurations__0__Context__WORKFLOW_NAME", DummyWorkflowType)
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
        if (_steeringInstance is not null) await _steeringInstance.DisposeAsync();
        if (_rabbitMq is not null) await _rabbitMq.DisposeAsync();
        if (_network is not null) await _network.DisposeAsync();
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
