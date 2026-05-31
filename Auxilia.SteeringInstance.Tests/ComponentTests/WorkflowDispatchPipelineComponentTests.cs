using System.Security.Cryptography;
using Auxilia.Messaging;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Tests.ComponentTests;

/// <summary>
/// Component tests for the workflow dispatch pipeline.
/// Builds a real DI container with <see cref="FakeMessageBusClient"/> and
/// <see cref="FakeWorkflowLauncher"/> so no Docker or RabbitMQ is required.
/// Exercises <see cref="WorkflowDispatcher"/>, <see cref="WorkflowAnnouncementHandler"/>,
/// and <see cref="WorkflowRegistrationHandler"/> together.
/// </summary>
[TestFixture]
[Category("Component")]
public class WorkflowDispatchPipelineComponentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private IHost _host = null!;
    private FakeMessageBusClient _bus = null!;
    private FakeWorkflowLauncher _launcher = null!;
    private SlotConfigurationStore _slotStore = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new FakeMessageBusClient();
        _launcher = new FakeWorkflowLauncher();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMessageBusClient>(_bus);
                services.AddSingleton<IWorkflowLauncher>(_launcher);

                // Launcher settings — internal network alias
                services.Configure<DockerWorkflowLauncherSettings>(s =>
                {
                    s.NetworkName    = "test-net";
                    s.RabbitMqHost   = "rabbitmq";
                    s.RabbitMqPort   = 5672;
                    s.RabbitMqUserName = "guest";
                    s.RabbitMqPassword = "guest";
                });

                // Runner profile — allow everything (no env requirement failures)
                services.Configure<RunnerProfile>(p =>
                {
                    p.AvailableTools = new HashSet<string> { "git" };
                    p.OpenPorts = new HashSet<int>();
                });

                services.AddSingleton<SlotConfigurationStore>();
                services.AddSingleton<SignalHandlerStore>();
                services.AddSingleton<WorkflowSchemaStore>();
                services.AddSingleton<PendingWorkflowPackageStore>();
                services.AddSingleton<WorkflowInstanceRegistry>();
                services.AddSingleton<DirtyConfigurationDetector>();
                services.AddSingleton<EnvironmentValidator>();
                services.AddSingleton<ConfigurationResolver>();
                services.AddSingleton<WorkflowRegistrationHandler>();
                services.AddSingleton<WorkflowAnnouncementHandler>();
                services.AddSingleton<WorkflowDispatcher>();
            })
            .Build();

        _slotStore = _host.Services.GetRequiredService<SlotConfigurationStore>();

        // Start all handlers (mirrors what Program.cs does)
        var regHandler   = _host.Services.GetRequiredService<WorkflowRegistrationHandler>();
        var annoHandler  = _host.Services.GetRequiredService<WorkflowAnnouncementHandler>();
        var dispatcher   = _host.Services.GetRequiredService<WorkflowDispatcher>();

        await regHandler.StartAsync(CancellationToken.None);
        await annoHandler.StartAsync(CancellationToken.None);
        await dispatcher.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public void TearDown()
    {
        _host.Dispose();
    }

    // ------------------------------------------------------------------ Dispatcher tests

    [Test]
    public async Task WhenRunCommandPublished_WorkflowLauncherIsCalledWithCorrectImage()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "auxilia-my-workflow:latest",
            new Dictionary<string, string>());

        await _bus.SimulateReceivedAsync("workflow.run-commands", command);

        var launched = await _bus.WaitForConditionAsync(
            () => _launcher.Calls.Count > 0, Timeout);

        Assert.That(launched, Is.True, "Launcher was not called within the timeout.");
        Assert.That(_launcher.Calls[0].Image, Is.EqualTo("auxilia-my-workflow:latest"));
    }

    [Test]
    public async Task WhenRunCommandPublished_LauncherReceivesRabbitMqEnvVars()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "image:latest", new Dictionary<string, string>());

        await _bus.SimulateReceivedAsync("workflow.run-commands", command);

        await _bus.WaitForConditionAsync(() => _launcher.Calls.Count > 0, Timeout);

        var env = _launcher.Calls[0].EnvironmentVariables;
        Assert.That(env["RabbitMq__Host"],     Is.EqualTo("rabbitmq"));
        Assert.That(env["RabbitMq__Port"],     Is.EqualTo("5672"));
        Assert.That(env["RabbitMq__UserName"], Is.EqualTo("guest"));
        Assert.That(env["RabbitMq__Password"], Is.EqualTo("guest"));
    }

    [Test]
    public async Task WhenRunCommandPublished_ContextIsPropagatedAsEnvVars()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "image:latest",
            new Dictionary<string, string> { ["REPO_URL"] = "https://example.com/repo" });

        await _bus.SimulateReceivedAsync("workflow.run-commands", command);

        await _bus.WaitForConditionAsync(() => _launcher.Calls.Count > 0, Timeout);

        Assert.That(_launcher.Calls[0].EnvironmentVariables["WORKFLOW_CONTEXT__REPO_URL"],
            Is.EqualTo("https://example.com/repo"));
    }

    // ------------------------------------------------------------------ Announcement handler tests

    [Test]
    public async Task WhenWorkflowAnnounces_RunDirectiveIsPublishedToResponseTopic()
    {
        var instanceId = Guid.NewGuid();
        var announcement = new WorkflowAnnouncementMessage(
            instanceId, "test-wf", "pubkey", "my-response-topic");

        await _bus.SimulateReceivedAsync("workflow.announcements", announcement);

        var responded = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == "my-response-topic"),
            Timeout);

        Assert.That(responded, Is.True, "No directive published to response topic within timeout.");

        var (_, msg) = _bus.PublishedMessages.First(m => m.Topic == "my-response-topic");
        var directive = msg as WorkflowDirective;
        Assert.That(directive, Is.Not.Null);
        Assert.That(directive!.Directive, Is.EqualTo(WorkflowDirectiveKind.Run));
        Assert.That(directive.WorkflowInstanceId, Is.EqualTo(instanceId));
    }

    // ------------------------------------------------------------------ No-slot registration tests

    [Test]
    public async Task WhenNoSlotWorkflowRegisters_ConfigurationResponseIsSuccess()
    {
        var instanceId = Guid.NewGuid();
        var responseTopic = $"resp-{instanceId:N}";

        var request = new WorkflowRegistrationRequest(
            instanceId,
            new WorkflowManifest("slotless-wf", instanceId.ToString(),
                [], // no slots
                [], string.Empty, [], []),
            AnyPublicKey(),
            responseTopic);

        await _bus.SimulateReceivedAsync("workflow-registration", request);

        var responded = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == responseTopic),
            Timeout);

        Assert.That(responded, Is.True);
        var (_, msg) = _bus.PublishedMessages.First(m => m.Topic == responseTopic);
        var response = msg as WorkflowConfigurationResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Slots, Is.Empty);
    }

    // ------------------------------------------------------------------ Slot config seeding tests

    [Test]
    public async Task WhenSlotConfigSeeded_ConfigurationResponseContainsThatSlot()
    {
        // Seed the store directly (mirrors what Program.cs does from appsettings)
        _slotStore.UpsertConfiguration("seeded-workflow",
            new StoredSlotConfiguration(
                "source-control", "LocalGit",
                new Dictionary<string, string> { ["RepositoryPath"] = "/repos/test" },
                ConfigurationStatus.Valid));

        var instanceId = Guid.NewGuid();
        var responseTopic = $"resp-{instanceId:N}";

        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var request = new WorkflowRegistrationRequest(
            instanceId,
            new WorkflowManifest("seeded-workflow", instanceId.ToString(),
                [new SlotDefinition("source-control", null) { ServiceType = typeof(object) }], // declares the slot
                [], string.Empty, [], []),
            publicKey,
            responseTopic);

        await _bus.SimulateReceivedAsync("workflow-registration", request);

        var responded = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == responseTopic),
            Timeout);

        Assert.That(responded, Is.True);
        var (_, msg) = _bus.PublishedMessages.First(m => m.Topic == responseTopic);
        var response = msg as WorkflowConfigurationResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.True);
        Assert.That(response.Slots.ContainsKey("source-control"), Is.True);
        Assert.That(response.Slots["source-control"].ProviderType, Is.EqualTo("LocalGit"));
    }

    [Test]
    public async Task WhenWorkflowHasSlotsButNoConfigSeeded_ConfigurationResponseIsFailure()
    {
        var instanceId = Guid.NewGuid();
        var responseTopic = $"resp-{instanceId:N}";

        var request = new WorkflowRegistrationRequest(
            instanceId,
            new WorkflowManifest("unconfigured-workflow", instanceId.ToString(),
                [new SlotDefinition("source-control", null) { ServiceType = typeof(object) }], // declares a slot
                [], string.Empty, [], []),
            AnyPublicKey(),
            responseTopic);

        await _bus.SimulateReceivedAsync("workflow-registration", request);

        var responded = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == responseTopic),
            Timeout);

        Assert.That(responded, Is.True);
        var (_, msg) = _bus.PublishedMessages.First(m => m.Topic == responseTopic);
        var response = msg as WorkflowConfigurationResponse;
        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Success, Is.False);
        Assert.That(response.ErrorMessage, Does.Contain("No slot configurations"));
    }

    // ------------------------------------------------------------------ helpers

    private static string AnyPublicKey()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }
}

