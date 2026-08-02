using System.Net.Http.Headers;
using Auxilia.Messaging;
using Auxilia.SystemTestSuite.WorkflowDispatch;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.Failover;

/// <summary>
/// Environment for Core.Runner failover system tests (ARCHITECTURE §14.2): two Core.Runners
/// compete on one command queue (each Mongo-backed for its own state), and a <b>Core.Api</b>
/// container runs the bus-based failover monitor (<c>RunnerHeartbeat</c> liveness +
/// <c>RunnerLivenessTracker</c> + <c>FailoverMonitor</c>). The failover monitor never reads a
/// runner's database — it fails over runs from the Core's OWN store, so runs under test are
/// dispatched through the Core Run API (they carry the stashed dispatch command needed for
/// re-dispatch). Heartbeat/monitor intervals are tightened so a failover completes within seconds.
/// </summary>
[SetUpFixture]
public class FailoverEnvironment
{
    internal const string CommandQueue    = "workflow.run-commands-failover";
    internal const string CoreApiImageName = "auxilia-core-api:system-test";
    internal const string BootstrapApiKey  = "aux-system-test-key-failover-0123456789";
    private  const string RabbitMqAlias    = "rabbitmq";
    private  const string MongoAlias       = "mongo";
    private  const string CoreApiAlias     = "core-api";
    private  const string DockerSocket     = "/var/run/docker.sock";

    private static readonly string NetworkName =
        $"auxilia-failover-{Guid.NewGuid():N}".Substring(0, 30);

    private INetwork          _network  = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private MongoDbContainer  _mongoDb  = null!;

    /// <summary>The Core.Api instance hosting the failover monitor (replaces the BackendService monitor).</summary>
    public static IContainer CoreApi { get; private set; } = null!;
    public static IContainer Runner1 { get; private set; } = null!;
    public static IContainer Runner2 { get; private set; } = null!;
    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    public static string MongoConnectionString { get; private set; } = null!;
    /// <summary>Authenticated (bootstrap Administrator) client for the Core Run API.</summary>
    public static HttpClient CoreApiClient { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Sequential on purpose: parallel docker builds have wedged the Docker Desktop daemon on
        // developer machines; layer caching makes the sequential cost negligible.
        await WorkflowDispatchEnvironment.BuildImageAsync(
            WorkflowDispatchEnvironment.RunnerImageName, "Source/Platform/Auxilia.Core.Runner/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            CoreApiImageName, "Source/Platform/Auxilia.Core.Api/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            WorkflowDispatchEnvironment.DummyWorkflowsImageName, "Tests/System/Auxilia.Workflows.Testing/Dockerfile");

        _network = new NetworkBuilder().WithName(NetworkName).Build();
        await _network.CreateAsync();

        _rabbitMq = new RabbitMqBuilder("rabbitmq:3.13-management")
            .WithUsername("guest").WithPassword("guest")
            .WithNetwork(_network).WithNetworkAliases(RabbitMqAlias)
            .Build();
        _mongoDb = new MongoDbBuilder("mongo:8.0")
            .WithNetwork(_network).WithNetworkAliases(MongoAlias)
            .WithUsername(string.Empty).WithPassword(string.Empty) // no auth — test only
            .Build();
        await Task.WhenAll(_rabbitMq.StartAsync(), _mongoDb.StartAsync());

        MongoConnectionString = _mongoDb.GetConnectionString();

        Runner1 = BuildRunner("fo-1");
        Runner2 = BuildRunner("fo-2");

        // Core.Api owns the failover monitor. Its own (InMemory) store is never shared with the
        // runners — it tracks runs via the WorkflowStatusEvent fanout and runner liveness via the
        // RunnerHeartbeat exchange, then fails over orphaned runs from that store.
        CoreApi = new ContainerBuilder(CoreApiImageName)
            .WithNetwork(_network)
            .WithNetworkAliases(CoreApiAlias)
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("PlatformData__Backend", "InMemory")
            .WithEnvironment("PlatformData__ProtectionKeyBase64", Convert.ToBase64String(new byte[32]))
            .WithEnvironment("CoreSecurity__BootstrapApiKey", BootstrapApiKey)
            .WithEnvironment("CoreApi__AllowDispatchWithoutRunner", "true")
            // The Core dispatches (and re-dispatches on failover) onto the queue the runner pool consumes.
            .WithEnvironment("CoreApi__RunCommandQueue", CommandQueue)
            // Tightened so a failover completes within seconds (mirrors the old PlatformHost settings).
            .WithEnvironment("CoreApi__HeartbeatTimeoutSeconds",      "8")
            .WithEnvironment("CoreApi__FailoverScanIntervalSeconds",  "2")
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__WorkflowType", "sleeping-workflow")
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__PackageUri",
                $"docker://{WorkflowDispatch.WorkflowDispatchEnvironment.DummyWorkflowsImageName}")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("FailoverMonitor started"))
            .Build();

        await Task.WhenAll(
            Runner1.StartAsync(),
            Runner2.StartAsync(),
            CoreApi.StartAsync());

        MessageBusClient = await RabbitMqClient.CreateAsync(
            _rabbitMq.Hostname, _rabbitMq.GetMappedPublicPort(5672));

        CoreApiClient = new HttpClient
        {
            BaseAddress = new Uri($"http://localhost:{CoreApi.GetMappedPublicPort(8080)}")
        };
        CoreApiClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", BootstrapApiKey);
    }

    private IContainer BuildRunner(string suffix) =>
        new ContainerBuilder(WorkflowDispatchEnvironment.RunnerImageName)
            .WithNetwork(_network)
            .WithBindMount(DockerSocket, DockerSocket)
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("WorkflowLauncher__NetworkName",      NetworkName)
            .WithEnvironment("WorkflowLauncher__RabbitMqHost",     RabbitMqAlias)
            .WithEnvironment("WorkflowLauncher__RabbitMqPort",     "5672")
            .WithEnvironment("WorkflowLauncher__RabbitMqUserName", "guest")
            .WithEnvironment("WorkflowLauncher__RabbitMqPassword", "guest")
            .WithEnvironment("WorkflowDispatcher__CommandQueueName",      CommandQueue)
            .WithEnvironment("WorkflowDispatcher__RegistrationQueueName", $"workflow-registration-{suffix}")
            .WithEnvironment("WorkflowDispatcher__AnnouncementQueueName", $"workflow.announcements-{suffix}")
            .WithEnvironment("WorkflowDispatcher__SlotActivationQueueName", $"workflow-slot-activation-{suffix}")
            .WithEnvironment("WorkflowDispatcher__HeartbeatIntervalSeconds", "2")
            .WithEnvironment("PlatformData__Backend",               "MongoDb")
            .WithEnvironment("PlatformData__MongoConnectionString", $"mongodb://{MongoAlias}:27017")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("WorkflowDispatcher started")
                .UntilMessageIsLogged("Runner heartbeat started"))
            .Build();

    /// <summary>Reads the Runner ServiceId from a container's startup log.</summary>
    public static async Task<Guid> ServiceIdOfAsync(IContainer runner)
    {
        var (stdout, stderr) = await runner.GetLogsAsync();
        var logs = stdout + stderr;
        const string marker = "CoreRunner ServiceId=";
        var index = logs.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException("ServiceId log line not found in Core.Runner logs.");
        return Guid.Parse(logs.Substring(index + marker.Length, 36));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        CoreApiClient?.Dispose();
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();
        if (CoreApi is not null) await CoreApi.DisposeAsync();
        await Runner1.DisposeAsync();
        await Runner2.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _mongoDb.DisposeAsync();
        await _network.DisposeAsync();
    }
}
