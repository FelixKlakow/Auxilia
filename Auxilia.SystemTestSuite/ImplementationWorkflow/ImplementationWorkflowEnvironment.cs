using System.Diagnostics;
using Auxilia.Messaging;
using Auxilia.SystemTestSuite.WorkflowDispatch;
using Auxilia.Workflows.Messaging.Messages;
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

    private IContainer        _happyRunner = null!;
    private IContainer        _edgeRunner  = null!;
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

        // Host-side publishes share a dependency graph — run sequentially to avoid obj/
        // contention (CS2012); the Docker builds compile inside containers and stay parallel.
        var imageBuilds = Task.WhenAll(
            WorkflowDispatchEnvironment.BuildImageAsync(
                WorkflowDispatchEnvironment.RunnerImageName,
                "Source/Auxilia.Core.Runner/Dockerfile"),
            WorkflowDispatchEnvironment.BuildImageAsync(
                ProductionImageName,
                "Source/Auxilia.ImplementationWorkflow/Dockerfile"));
        await PublishProjectAsync(
            "Auxilia.FakeSlots.Implementation.Happy/Auxilia.FakeSlots.Implementation.Happy.csproj",
            _happyPublishDir);
        await PublishProjectAsync(
            "Auxilia.FakeSlots.Implementation.AgentFailure/Auxilia.FakeSlots.Implementation.AgentFailure.csproj",
            _edgePublishDir);
        await imageBuilds;

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

        _happyRunner = new ContainerBuilder(WorkflowDispatchEnvironment.RunnerImageName)
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
            .WithEnvironment("WorkflowDispatcher__AnnouncementQueueName", "workflow.announcements-impl-happy")
            .WithEnvironment("WorkflowDispatcher__SlotActivationQueueName", "workflow-slot-activation-impl-happy")
            .WithEnvironment("WorkflowLauncher__ExtraEnvironmentVariables__AUXILIA_DEVELOPER_MODE", "1")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("WorkflowDispatcher started")
                .UntilMessageIsLogged("SlotConfigurationSeedHandler started"))
            .Build();

        _edgeRunner = new ContainerBuilder(WorkflowDispatchEnvironment.RunnerImageName)
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
            .WithEnvironment("WorkflowDispatcher__AnnouncementQueueName", "workflow.announcements-impl-edge")
            .WithEnvironment("WorkflowDispatcher__SlotActivationQueueName", "workflow-slot-activation-impl-edge")
            .WithEnvironment("WorkflowLauncher__ExtraEnvironmentVariables__AUXILIA_DEVELOPER_MODE", "1")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("WorkflowDispatcher started")
                .UntilMessageIsLogged("SlotConfigurationSeedHandler started"))
            .Build();

        await Task.WhenAll(
            _happyRunner.StartAsync(),
            _edgeRunner.StartAsync());

        MessageBusClient = await RabbitMqClient.CreateAsync(RabbitMqHost, RabbitMqPort);

        // Seed queues are per-instance: CommandQueueName + "-slot-seed".
        // Using PublishAsync (direct delivery) instead of PublishToExchangeAsync (fanout broadcast)
        // ensures each container receives only its own provider and slot configurations.
        // Both containers use the same workflow-type name "implementation-workflow",
        // so fanout broadcast would cause last-write-wins collisions on shared (workflowType, slotName) keys.
        var happySeedBase = HappyCommandQueue + "-slot-seed";
        var edgeSeedBase  = EdgeCommandQueue  + "-slot-seed";

        // Happy container: register provider + seed all six slots
        await MessageBusClient.PublishAsync(happySeedBase + ".register",
            new RegisterSlotProviderCommand(
                "fake-implementation-happy",
                $"{ContainerPluginsDir}/Auxilia.FakeSlots.Implementation.Happy.slothandler.dll"));
        foreach (var slotName in new[] { "repository", "task-source", "implementation-agent",
                                          "reviewer-agent", "test-runner", "pull-request" })
            await MessageBusClient.PublishAsync(happySeedBase + ".upsert",
                new UpsertSlotConfigurationCommand(
                    "implementation-workflow", slotName, "fake-implementation-happy",
                    new Dictionary<string, string>()));

        // Edge container: register provider + seed all six slots
        await MessageBusClient.PublishAsync(edgeSeedBase + ".register",
            new RegisterSlotProviderCommand(
                "fake-implementation-agent-failure",
                $"{ContainerPluginsDir}/Auxilia.FakeSlots.Implementation.AgentFailure.slothandler.dll"));
        foreach (var slotName in new[] { "repository", "task-source", "implementation-agent",
                                          "reviewer-agent", "test-runner", "pull-request" })
            await MessageBusClient.PublishAsync(edgeSeedBase + ".upsert",
                new UpsertSlotConfigurationCommand(
                    "implementation-workflow", slotName, "fake-implementation-agent-failure",
                    new Dictionary<string, string>()));

        // Allow propagation window: seed queue messages are processed asynchronously over the network
        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (MessageBusClient is IAsyncDisposable d) await d.DisposeAsync();
        await _happyRunner.DisposeAsync();
        await _edgeRunner.DisposeAsync();
        await _rabbitMq.DisposeAsync();
        await _network.DisposeAsync();
        if (Directory.Exists(_happyPublishDir)) Directory.Delete(_happyPublishDir, recursive: true);
        if (Directory.Exists(_edgePublishDir))  Directory.Delete(_edgePublishDir,  recursive: true);
    }

    private static async Task PublishProjectAsync(string projectRelativePath, string outputDir)
    {
        // -nodeReuse:false + UseSharedCompilation=false: persistent MSBuild/Roslyn worker
        // processes inherit the redirected stdout/stderr pipes; with node reuse the workers
        // outlive the publish and ReadToEndAsync stalls until their idle timeout (~15 min).
        var psi = new ProcessStartInfo("dotnet",
            $"publish {projectRelativePath} -c Release -o {outputDir} --no-self-contained -nodeReuse:false -p:UseSharedCompilation=false")
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

