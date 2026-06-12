using Auxilia.Messaging;
using Auxilia.SystemTestSuite.WorkflowDispatch;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.Failover;

/// <summary>
/// Environment for Steering Instance failover system tests (ARCHITECTURE §14.2):
/// two Steering Instances compete on one command queue, sharing platform state via MongoDB;
/// the Backend Service watches their heartbeats and fails over orphaned runs.
/// Heartbeat/monitor intervals are tightened so a failover completes within seconds.
/// </summary>
[SetUpFixture]
public class FailoverEnvironment
{
    internal const string CommandQueue = "workflow.run-commands-failover";
    private  const string RabbitMqAlias = "rabbitmq";
    private  const string MongoAlias    = "mongo";
    private  const string DockerSocket  = "/var/run/docker.sock";

    private static readonly string NetworkName =
        $"auxilia-failover-{Guid.NewGuid():N}".Substring(0, 30);

    private INetwork          _network  = null!;
    private RabbitMqContainer _rabbitMq = null!;
    private MongoDbContainer  _mongoDb  = null!;

    public static IContainer Backend { get; private set; } = null!;
    public static IContainer SteeringInstance1 { get; private set; } = null!;
    public static IContainer SteeringInstance2 { get; private set; } = null!;
    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    public static string MongoConnectionString { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        // Sequential on purpose: three parallel docker builds have wedged the Docker Desktop
        // daemon on developer machines; layer caching makes the sequential cost negligible.
        await WorkflowDispatchEnvironment.BuildImageAsync(
            WorkflowDispatchEnvironment.SteeringImageName, "Source/Auxilia.SteeringInstance/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            "auxilia-backendservice:system-test", "Source/Auxilia.BackendService/Dockerfile");
        await WorkflowDispatchEnvironment.BuildImageAsync(
            WorkflowDispatchEnvironment.DummyWorkflowsImageName, "Auxilia.Workflows.Testing/Dockerfile");

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

        SteeringInstance1 = BuildSteeringInstance("fo-1");
        SteeringInstance2 = BuildSteeringInstance("fo-2");

        Backend = new ContainerBuilder("auxilia-backendservice:system-test")
            .WithNetwork(_network)
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("PlatformData__Backend",               "MongoDb")
            .WithEnvironment("PlatformData__MongoConnectionString", $"mongodb://{MongoAlias}:27017")
            .WithEnvironment("PlatformHost__HeartbeatTimeoutSeconds", "8")
            .WithEnvironment("PlatformHost__MonitorIntervalSeconds",  "2")
            .WithEnvironment("PlatformHost__CommandQueueName",        CommandQueue)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("HeartbeatMonitor started"))
            .Build();

        await Task.WhenAll(
            SteeringInstance1.StartAsync(),
            SteeringInstance2.StartAsync(),
            Backend.StartAsync());

        MessageBusClient = await RabbitMqClient.CreateAsync(
            _rabbitMq.Hostname, _rabbitMq.GetMappedPublicPort(5672));
    }

    private IContainer BuildSteeringInstance(string suffix) =>
        new ContainerBuilder(WorkflowDispatchEnvironment.SteeringImageName)
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
                .UntilMessageIsLogged("Steering heartbeat started"))
            .Build();

    /// <summary>Reads the SteeringInstance ServiceId from a container's startup log.</summary>
    public static async Task<Guid> ServiceIdOfAsync(IContainer steeringInstance)
    {
        var (stdout, stderr) = await steeringInstance.GetLogsAsync();
        var logs = stdout + stderr;
        const string marker = "SteeringInstance ServiceId=";
        var index = logs.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            throw new InvalidOperationException("ServiceId log line not found in Steering Instance logs.");
        return Guid.Parse(logs.Substring(index + marker.Length, 36));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();
        await Backend.DisposeAsync();
        await SteeringInstance1.DisposeAsync();
        await SteeringInstance2.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _mongoDb.DisposeAsync();
        await _network.DisposeAsync();
    }
}
