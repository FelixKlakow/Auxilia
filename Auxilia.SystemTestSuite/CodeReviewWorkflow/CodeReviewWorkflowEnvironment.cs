using System.Diagnostics;
using Auxilia.Messaging;
using Auxilia.SystemTestSuite.WorkflowDispatch;
using Auxilia.Workflows.Messaging.Messages;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Testcontainers.RabbitMq;

namespace Auxilia.SystemTestSuite.CodeReviewWorkflow;

[SetUpFixture]
public class CodeReviewWorkflowEnvironment
{
    internal const string ProductionImageName = "auxilia-code-review-workflow:system-test";
    internal const string HappyCommandQueue   = "workflow.run-commands-crw-happy";
    internal const string EdgeCommandQueue    = "workflow.run-commands-crw-edge";
    /// <summary>Named workflow configuration (#18) seeded on the happy instance.</summary>
    internal const string ConfigurationName   = "code-review-alpha";
    private  const string RabbitMqAlias       = "rabbitmq";
    private  const string RabbitMqImage       = "rabbitmq:3.13-management";
    private  const string DockerSocket        = "/var/run/docker.sock";
    private  const string ContainerPluginsDir = "/slot-plugins";
    /// <summary>JSON platform-data directory inside the happy container (deterministic for asserts).</summary>
    internal const string HappyPlatformDataDir = "/platform-data";

    private static readonly string NetworkName =
        $"auxilia-crw-{Guid.NewGuid():N}".Substring(0, 30);

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
    /// <summary>The happy runner container, exposed so tests can inspect its platform data.</summary>
    public static IContainer        HappyRunner { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _happyPublishDir = Path.Combine(Path.GetTempPath(), $"auxilia-{Guid.NewGuid():N}");
        _edgePublishDir  = Path.Combine(Path.GetTempPath(), $"auxilia-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_happyPublishDir);
        Directory.CreateDirectory(_edgePublishDir);

        // The two fake-slot projects share their dependency graph (both transitively build
        // Auxilia.CodeReview.Workflow) — parallel `dotnet publish` races on the same obj/
        // directory (CS2012). Host-side publishes must run sequentially; the Docker image
        // builds compile inside containers and can stay parallel.
        var imageBuilds = Task.WhenAll(
            WorkflowDispatchEnvironment.BuildImageAsync(
                WorkflowDispatchEnvironment.RunnerImageName,
                "Source/Auxilia.Core.Runner/Dockerfile"),
            WorkflowDispatchEnvironment.BuildImageAsync(
                ProductionImageName,
                "Source/Auxilia.CodeReview.Workflow/Dockerfile"));
        await PublishProjectAsync(
            "Auxilia.FakeSlots.CodeReview.Happy/Auxilia.FakeSlots.CodeReview.Happy.csproj",
            _happyPublishDir);
        await PublishProjectAsync(
            "Auxilia.FakeSlots.CodeReview.WriteBackFailure/Auxilia.FakeSlots.CodeReview.WriteBackFailure.csproj",
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
            .WithEnvironment("PlatformData__JsonDirectory", HappyPlatformDataDir)
            .WithEnvironment("WorkflowDispatcher__CommandQueueName",      HappyCommandQueue)
            .WithEnvironment("WorkflowDispatcher__RegistrationQueueName", "workflow-registration-crw-happy")
            .WithEnvironment("WorkflowDispatcher__AnnouncementQueueName", "workflow.announcements-crw-happy")
            .WithEnvironment("WorkflowDispatcher__SlotActivationQueueName", "workflow-slot-activation-crw-happy")
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
            .WithEnvironment("WorkflowDispatcher__RegistrationQueueName", "workflow-registration-crw-edge")
            .WithEnvironment("WorkflowDispatcher__AnnouncementQueueName", "workflow.announcements-crw-edge")
            .WithEnvironment("WorkflowDispatcher__SlotActivationQueueName", "workflow-slot-activation-crw-edge")
            .WithEnvironment("WorkflowLauncher__ExtraEnvironmentVariables__AUXILIA_DEVELOPER_MODE", "1")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("WorkflowDispatcher started")
                .UntilMessageIsLogged("SlotConfigurationSeedHandler started"))
            .Build();

        await Task.WhenAll(
            _happyRunner.StartAsync(),
            _edgeRunner.StartAsync());

        HappyRunner = _happyRunner;
        MessageBusClient = await RabbitMqClient.CreateAsync(RabbitMqHost, RabbitMqPort);

        var happySeedBase = HappyCommandQueue + "-slot-seed";
        var edgeSeedBase  = EdgeCommandQueue  + "-slot-seed";

        // Happy container: register provider + seed all six slots
        var slotNames = new[] { "repository", "pull-request", "work-items",
                                "primary-reviewer", "secondary-reviewer", "workflow-bootstrap" };
        await MessageBusClient.PublishAsync(happySeedBase + ".register",
            new RegisterSlotProviderCommand(
                "fake-code-review-happy",
                $"{ContainerPluginsDir}/Auxilia.FakeSlots.CodeReview.Happy.slothandler.dll"));
        foreach (var slotName in slotNames)
            await MessageBusClient.PublishAsync(happySeedBase + ".upsert",
                new UpsertSlotConfigurationCommand(
                    "pull-request-code-review", slotName, "fake-code-review-happy",
                    new Dictionary<string, string>()));

        // Named workflow configuration (#18) on the happy instance — bindings mirror the
        // global slot configurations seeded above.
        await MessageBusClient.PublishAsync(happySeedBase + ".upsert-configuration",
            new UpsertWorkflowConfigurationCommand(
                ConfigurationName, "Code Review Alpha", "pull-request-code-review",
                $"docker://{ProductionImageName}", Enabled: true,
                slotNames
                    .Select(slotName => new SlotBindingSeed(
                        slotName, "fake-code-review-happy", new Dictionary<string, string>()))
                    .ToList()));

        // Edge container: register provider + seed all six slots
        await MessageBusClient.PublishAsync(edgeSeedBase + ".register",
            new RegisterSlotProviderCommand(
                "fake-code-review-write-back-failure",
                $"{ContainerPluginsDir}/Auxilia.FakeSlots.CodeReview.WriteBackFailure.slothandler.dll"));
        foreach (var slotName in new[] { "repository", "pull-request", "work-items",
                                          "primary-reviewer", "secondary-reviewer", "workflow-bootstrap" })
            await MessageBusClient.PublishAsync(edgeSeedBase + ".upsert",
                new UpsertSlotConfigurationCommand(
                    "pull-request-code-review", slotName, "fake-code-review-write-back-failure",
                    new Dictionary<string, string>()));

        await Task.Delay(TimeSpan.FromMilliseconds(500)); // propagation window
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
            WorkingDirectory      = RepoRoot,
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