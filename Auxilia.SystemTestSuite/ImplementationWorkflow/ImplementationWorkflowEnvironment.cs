using System.Diagnostics;
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
    internal const string ProductionImageName = "auxilia-implementation-workflow:system-test";
    internal const string HappyCommandQueue   = "workflow.run-commands-impl-happy";
    internal const string EdgeCommandQueue    = "workflow.run-commands-impl-edge";
    private  const string RabbitMqAlias       = "rabbitmq";
    private  const string RabbitMqImage       = "rabbitmq:3.13-management";
    private  const string DockerSocket        = "/var/run/docker.sock";
    private  const string ContainerPluginsDir = "/slot-plugins";

    private static readonly string NetworkName =
        $"auxilia-implwf-{Guid.NewGuid():N}".Substring(0, 30);

    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private IContainer        _happySteeringInstance = null!;
    private IContainer        _edgeSteeringInstance  = null!;
    private INetwork          _network               = null!;
    private RabbitMqContainer _rabbitMq              = null!;
    private string            _happyPublishDir       = null!;
    private string            _edgePublishDir        = null!;

    public static IMessageBusClient MessageBusClient { get; private set; } = null!;
    public static string            RabbitMqHost     { get; private set; } = null!;
    public static int               RabbitMqPort     { get; private set; }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _happyPublishDir = Path.Combine(Path.GetTempPath(), $"auxilia-{Guid.NewGuid():N}");
        _edgePublishDir  = Path.Combine(Path.GetTempPath(), $"auxilia-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_happyPublishDir);
        Directory.CreateDirectory(_edgePublishDir);

        await Task.WhenAll(
            PublishProjectAsync(
                "Auxilia.FakeSlots.Implementation.Happy/Auxilia.FakeSlots.Implementation.Happy.csproj",
                _happyPublishDir),
            PublishProjectAsync(
                "Auxilia.FakeSlots.Implementation.AgentFailure/Auxilia.FakeSlots.Implementation.AgentFailure.csproj",
                _edgePublishDir),
            WorkflowDispatchEnvironment.BuildImageAsync(
                WorkflowDispatchEnvironment.SteeringImageName,
                "Source/Auxilia.SteeringInstance/Dockerfile"),
            WorkflowDispatchEnvironment.BuildImageAsync(
                ProductionImageName,
                "Source/Auxilia.ImplementationWorkflow/Dockerfile"));

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

        _happySteeringInstance = new ContainerBuilder(WorkflowDispatchEnvironment.SteeringImageName)
            .WithNetwork(_network)
            .WithBindMount(DockerSocket, DockerSocket)
            .WithBindMount(_happyPublishDir, ContainerPluginsDir)
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("WorkflowLauncher__NetworkName",      NetworkName)
            .WithEnvironment("WorkflowLauncher__RabbitMqHost",     RabbitMqAlias)
            .WithEnvironment("WorkflowLauncher__RabbitMqPort",     "5672")
            .WithEnvironment("WorkflowLauncher__RabbitMqUserName", "guest")
            .WithEnvironment("WorkflowLauncher__RabbitMqPassword", "guest")
            .WithEnvironment("WorkflowDispatcher__CommandQueueName",      HappyCommandQueue)
            .WithEnvironment("WorkflowDispatcher__RegistrationQueueName", "workflow-registration-impl-happy")
            .WithEnvironment("WorkflowLauncher__ExtraEnvironmentVariables__AUXILIA_DEVELOPER_MODE", "1")
            .WithEnvironment("WorkflowLauncher__SlotPackages__fake-implementation-happy",
                $"{ContainerPluginsDir}/Auxilia.FakeSlots.Implementation.Happy.slothandler.dll")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__0__SlotName",    "repository")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__0__ProviderType","fake-implementation-happy")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__1__SlotName",    "task-source")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__1__ProviderType","fake-implementation-happy")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__2__SlotName",    "implementation-agent")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__2__ProviderType","fake-implementation-happy")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__3__SlotName",    "reviewer-agent")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__3__ProviderType","fake-implementation-happy")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__4__SlotName",    "test-runner")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__4__ProviderType","fake-implementation-happy")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__5__SlotName",    "pull-request")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__5__ProviderType","fake-implementation-happy")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("WorkflowDispatcher started"))
            .Build();

        _edgeSteeringInstance = new ContainerBuilder(WorkflowDispatchEnvironment.SteeringImageName)
            .WithNetwork(_network)
            .WithBindMount(DockerSocket, DockerSocket)
            .WithBindMount(_edgePublishDir, ContainerPluginsDir)
            .WithEnvironment("RabbitMq__Host",     RabbitMqAlias)
            .WithEnvironment("RabbitMq__Port",     "5672")
            .WithEnvironment("RabbitMq__UserName", "guest")
            .WithEnvironment("RabbitMq__Password", "guest")
            .WithEnvironment("WorkflowLauncher__NetworkName",      NetworkName)
            .WithEnvironment("WorkflowLauncher__RabbitMqHost",     RabbitMqAlias)
            .WithEnvironment("WorkflowLauncher__RabbitMqPort",     "5672")
            .WithEnvironment("WorkflowLauncher__RabbitMqUserName", "guest")
            .WithEnvironment("WorkflowLauncher__RabbitMqPassword", "guest")
            .WithEnvironment("WorkflowDispatcher__CommandQueueName",      EdgeCommandQueue)
            .WithEnvironment("WorkflowDispatcher__RegistrationQueueName", "workflow-registration-impl-edge")
            .WithEnvironment("WorkflowLauncher__ExtraEnvironmentVariables__AUXILIA_DEVELOPER_MODE", "1")
            .WithEnvironment("WorkflowLauncher__SlotPackages__fake-implementation-agent-failure",
                $"{ContainerPluginsDir}/Auxilia.FakeSlots.Implementation.AgentFailure.slothandler.dll")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__0__SlotName",    "repository")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__0__ProviderType","fake-implementation-agent-failure")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__1__SlotName",    "task-source")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__1__ProviderType","fake-implementation-agent-failure")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__2__SlotName",    "implementation-agent")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__2__ProviderType","fake-implementation-agent-failure")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__3__SlotName",    "reviewer-agent")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__3__ProviderType","fake-implementation-agent-failure")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__4__SlotName",    "test-runner")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__4__ProviderType","fake-implementation-agent-failure")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__5__SlotName",    "pull-request")
            .WithEnvironment("SlotConfigurations__Workflows__implementation-workflow__5__ProviderType","fake-implementation-agent-failure")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("WorkflowDispatcher started"))
            .Build();

        await Task.WhenAll(
            _happySteeringInstance.StartAsync(),
            _edgeSteeringInstance.StartAsync());

        MessageBusClient = await RabbitMqClient.CreateAsync(RabbitMqHost, RabbitMqPort);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();
        await _happySteeringInstance.DisposeAsync();
        await _edgeSteeringInstance.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _network.DisposeAsync();
        if (Directory.Exists(_happyPublishDir)) Directory.Delete(_happyPublishDir, recursive: true);
        if (Directory.Exists(_edgePublishDir))  Directory.Delete(_edgePublishDir,  recursive: true);
    }

    private static async Task PublishProjectAsync(string projectRelativePath, string outputDir)
    {
        var psi = new ProcessStartInfo("dotnet",
            $"publish {projectRelativePath} -c Release -o {outputDir} --no-self-contained")
        {
            WorkingDirectory       = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        };

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException("Failed to start dotnet publish.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet publish failed for {projectRelativePath} (exit {process.ExitCode}):\n{stdout}\n{stderr}");
    }
}

