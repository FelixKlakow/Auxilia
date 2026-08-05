using System.Net.Http.Headers;
using Auxilia.Messaging;
using Auxilia.SystemTestSuite.WorkflowDispatch;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.ReAdoption;

/// <summary>
/// Environment for the runner RE-ADOPTION system tests (ARCHITECTURE §6): ONE Mongo-backed
/// Core.Runner (the instance registry and persisted container ids must survive its restart)
/// plus a Core.Api whose failover clock is deliberately SLOW — the restarted runner must win
/// the race and re-claim its containers before any failover sweep would fire, which is exactly
/// the production contract (<c>ReadoptContainersOnStart</c> replaces reaping).
/// </summary>
[SetUpFixture]
public class ReAdoptionEnvironment
{
    internal const string CommandQueue    = "workflow.run-commands-readoption";
    internal const string BootstrapApiKey = "aux-system-test-key-readoption-0123456789";
    private  const string RabbitMqAlias   = "rabbitmq";
    private  const string MongoAlias      = "mongo";
    private  const string DockerSocket    = "/var/run/docker.sock";

    private static readonly string NetworkName =
        $"auxilia-readopt-{Guid.NewGuid():N}".Substring(0, 30);

    private INetwork          _network  = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private MongoDbContainer  _mongoDb  = null!;
    private IContainer        _coreApi  = null!;

    public static IContainer Runner { get; private set; } = null!;
    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    public static HttpClient CoreApiClient { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await WorkflowDispatchEnvironment.BuildImageAsync(
            WorkflowDispatchEnvironment.RunnerImageName, "Source/Platform/Auxilia.Core.Runner/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            Failover.FailoverEnvironment.CoreApiImageName, "Source/Platform/Auxilia.Core.Api/Dockerfile");
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

        Runner = new ContainerBuilder(WorkflowDispatchEnvironment.RunnerImageName)
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
            .WithEnvironment("WorkflowDispatcher__CommandQueueName",        CommandQueue)
            .WithEnvironment("WorkflowDispatcher__RegistrationQueueName",   "workflow-registration-readopt")
            .WithEnvironment("WorkflowDispatcher__AnnouncementQueueName",   "workflow.announcements-readopt")
            .WithEnvironment("WorkflowDispatcher__SlotActivationQueueName", "workflow-slot-activation-readopt")
            .WithEnvironment("WorkflowDispatcher__HeartbeatIntervalSeconds", "2")
            .WithEnvironment("PlatformData__Backend",               "MongoDb")
            .WithEnvironment("PlatformData__MongoConnectionString", $"mongodb://{MongoAlias}:27017")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("WorkflowDispatcher started")
                .UntilMessageIsLogged("Runner heartbeat started"))
            .Build();

        _coreApi = new ContainerBuilder(Failover.FailoverEnvironment.CoreApiImageName)
            .WithNetwork(_network)
            .WithEnvironment("ASPNETCORE_URLS", "http://+:8080")
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("PlatformData__Backend", "InMemory")
            .WithEnvironment("PlatformData__ProtectionKeyBase64", Convert.ToBase64String(new byte[32]))
            .WithEnvironment("CoreSecurity__BootstrapApiKey", BootstrapApiKey)
            .WithEnvironment("CoreApi__AllowDispatchWithoutRunner", "true")
            .WithEnvironment("CoreApi__RunCommandQueue", CommandQueue)
            // SLOW failover clock: the restart must be covered by re-adoption, not the sweep.
            .WithEnvironment("CoreApi__HeartbeatTimeoutSeconds",     "300")
            .WithEnvironment("CoreApi__FailoverScanIntervalSeconds", "60")
            .WithEnvironment("CoreApi__DispatchClaimTimeoutSeconds", "300")
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__WorkflowType", "sleeping-workflow")
            .WithEnvironment("CoreApi__StaticWorkflowTypes__0__PackageUri",
                $"docker://{WorkflowDispatchEnvironment.DummyWorkflowsImageName}")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("FailoverMonitor started"))
            .Build();

        await Task.WhenAll(Runner.StartAsync(), _coreApi.StartAsync());

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
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();
        if (_coreApi is not null) await _coreApi.DisposeAsync();
        if (Runner is not null) await Runner.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _mongoDb.DisposeAsync();
        await _network.DisposeAsync();
    }
}
