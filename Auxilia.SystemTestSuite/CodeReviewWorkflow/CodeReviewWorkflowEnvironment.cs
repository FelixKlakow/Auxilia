using Auxilia.Messaging;
using Auxilia.SystemTestSuite.WorkflowDispatch;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.CodeReviewWorkflow;

[SetUpFixture]
public class CodeReviewWorkflowEnvironment
{
    internal const string HarnessImageName = "auxilia-real-workflow-harness:system-test";
    private  const string RabbitMqAlias    = "rabbitmq";
    private  const string RabbitMqImage    = "rabbitmq:3.13-management";

    private static readonly string NetworkName =
        $"auxilia-crw-{Guid.NewGuid():N}".Substring(0, 30);

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
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__0__SlotName",    "repository")
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__0__ProviderType","fake-source-control")
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__1__SlotName",    "pull-request")
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__1__ProviderType","fake-pull-request")
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__2__SlotName",    "work-items")
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__2__ProviderType","fake-work-item")
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__3__SlotName",    "primary-reviewer")
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__3__ProviderType","fake-primary-ai")
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__4__SlotName",    "secondary-reviewer")
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__4__ProviderType","fake-secondary-ai")
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__5__SlotName",    "workflow-bootstrap")
            .WithEnvironment("SlotConfigurations__Workflows__pull-request-code-review__5__ProviderType","fake-workflow-bootstrap")
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
