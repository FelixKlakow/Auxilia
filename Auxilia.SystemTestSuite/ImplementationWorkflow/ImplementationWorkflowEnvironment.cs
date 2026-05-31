using Auxilia.Messaging;
using Auxilia.SystemTestSuite.WorkflowDispatch;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.ImplementationWorkflow;

[SetUpFixture]
public class ImplementationWorkflowEnvironment
{
    internal const string HarnessImageName = "auxilia-real-workflow-harness:system-test";
    private  const string RabbitMqAlias    = "rabbitmq";
    private  const string RabbitMqImage    = "rabbitmq:3.13-management";

    private static readonly string NetworkName =
        $"auxilia-implwf-{Guid.NewGuid():N}".Substring(0, 30);

    private IContainer        _steeringInstance = null!;
    private INetwork          _network          = null!;
    private RabbitMqContainer _rabbitMq         = null!;

    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    public static string            RabbitMqHost     { get; private set; } = null!;
    public static int               RabbitMqPort     { get; private set; }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await Task.WhenAll(
            WorkflowDispatchEnvironment.BuildImageAsync(
                WorkflowDispatchEnvironment.SteeringImageName,
                "Source/Auxilia.SteeringInstance/Dockerfile"),
            WorkflowDispatchEnvironment.BuildImageAsync(
                HarnessImageName,
                "Auxilia.Workflows.RealWorkflowHarness/Dockerfile"));

        _network = new NetworkBuilder()
            .WithName(NetworkName)
            .Build();
        await _network.CreateAsync();

        _rabbitMq = new RabbitMqBuilder(RabbitMqImage)
            .WithUsername("guest")
            .WithPassword("guest")
            .WithNetwork(_network)
            .WithNetworkAliases(RabbitMqAlias)
            .Build();
        await _rabbitMq.StartAsync();

        RabbitMqHost = _rabbitMq.Hostname;
        RabbitMqPort = _rabbitMq.GetMappedPublicPort(5672);

        const string dockerSocket = "/var/run/docker.sock";

        _steeringInstance = new ContainerBuilder(WorkflowDispatchEnvironment.SteeringImageName)
            .WithNetwork(_network)
            .WithBindMount(dockerSocket, dockerSocket)
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("WorkflowLauncher__NetworkName",      NetworkName)
            .WithEnvironment("WorkflowLauncher__RabbitMqHost",     RabbitMqAlias)
            .WithEnvironment("WorkflowLauncher__RabbitMqPort",     "5672")
            .WithEnvironment("WorkflowLauncher__RabbitMqUserName", "guest")
            .WithEnvironment("WorkflowLauncher__RabbitMqPassword", "guest")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__0__SlotName",    "repository")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__0__ProviderType","fake-repository")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__1__SlotName",    "task-source")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__1__ProviderType","fake-task-source")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__2__SlotName",    "implementation-agent")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__2__ProviderType","fake-implementation-agent")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__3__SlotName",    "reviewer-agent")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__3__ProviderType","fake-reviewer-agent")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__4__SlotName",    "test-runner")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__4__ProviderType","fake-test-runner")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__5__SlotName",    "pull-request")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__5__ProviderType","fake-pull-request")
            .WithWaitStrategy(
                Wait.ForUnixContainer().UntilMessageIsLogged("WorkflowDispatcher started"))
            .Build();
        await _steeringInstance.StartAsync();

        MessageBusClient = await RabbitMqClient.CreateAsync(RabbitMqHost, RabbitMqPort);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();
        await _steeringInstance.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _network.DisposeAsync();
    }
}
