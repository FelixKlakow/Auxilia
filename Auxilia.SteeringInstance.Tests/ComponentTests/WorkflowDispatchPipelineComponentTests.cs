using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Governance;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

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
    private SlotProviderRegistry _providerRegistry = null!;

    private static byte[] CreateMinimalPackageZip(string workflowType = "my-workflow")
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("package-manifest.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(JsonSerializer.Serialize(new
            {
                files = Array.Empty<object>(),
                signatureBase64 = "c2ln",
                publicKeyBase64 = "a2V5",
                executableRelativePath = "bin/my-workflow"
            }));
        }
        return ms.ToArray();
    }

    private sealed class StubHttpMessageHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
    }

    [SetUp]
    public async Task SetUp()
    {
        _bus = new FakeMessageBusClient();
        _launcher = new FakeWorkflowLauncher();

        var packageZip = CreateMinimalPackageZip();
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(f => f.CreateClient("workflow-packages"))
            .Returns(new HttpClient(new StubHttpMessageHandler(packageZip)));

        var developerMode = new Mock<IDeveloperModeProvider>();
        developerMode.Setup(d => d.IsActive).Returns(true);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMessageBusClient>(_bus);
                services.AddSingleton<IWorkflowLauncher>(_launcher);
                services.AddSingleton(httpClientFactory.Object);
                services.AddSingleton(developerMode.Object);
                services.AddSingleton<IWorkflowPackageVerifier, WorkflowPackageVerifier>();

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

                // These tests publish announcements/registrations directly (no dispatcher launch),
                // so they run in the unauthenticated dev mode. The authenticated handshake is
                // covered by AuthenticatedHandshakeComponentTests.
                services.Configure<WorkflowDispatcherSettings>(s => s.RequireInstanceToken = false);

                var platformData = new PlatformDataSettings { Backend = PlatformDataBackend.InMemory };
                services.AddPlatformEntity<WorkflowSchemaRecord>(platformData);
                services.AddPlatformEntity<WorkflowPackageRecord>(platformData);
                services.AddPlatformEntity<SlotConfigurationRecord>(platformData);
                services.AddPlatformEntity<SlotProviderRecord>(platformData);
                services.AddPlatformEntity<WorkflowConfigurationRecord>(platformData);
                services.AddPlatformEntity<SignalHandlerRecord>(platformData);
                services.AddPlatformEntity<WorkflowInstanceRecord>(platformData);
                services.AddPlatformEntity<AuditRecord>(platformData);
                services.AddSettingsProtection(platformData);
                services.AddSingleton<AuditLog>();
                services.AddGovernance(platformData, new Auxilia.Governance.GovernanceSettings());

                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<WorkflowStatusPublisher>();
                services.AddSingleton(new SteeringInstanceInfo(Guid.NewGuid(), DateTime.UtcNow));
                services.AddSingleton<WorkflowInstanceTokenRegistry>();
                services.AddSingleton<SlotConfigurationStore>();
                services.AddSingleton<WorkflowConfigurationStore>();
                services.AddSingleton<SlotProviderRegistry>();
                services.AddSingleton<SignalHandlerStore>();
                services.AddSingleton<WorkflowSchemaStore>();
                services.AddSingleton<WorkflowPackageStore>();
                services.AddSingleton<PendingWorkflowPackageStore>();
                services.AddSingleton<WorkflowInstanceRegistry>();
                services.AddSingleton<DirtyConfigurationDetector>();
                services.AddSingleton<EnvironmentValidator>();
                services.AddSingleton<ConfigurationResolver>();
                services.AddSingleton<WorkflowRegistrationHandler>();
                services.AddSingleton<WorkflowAnnouncementHandler>();
                services.AddSingleton<NetworkPolicyResolver>();
                services.AddSingleton<WorkspaceManager>();
                services.AddSingleton<WorkflowDispatcher>();
            })
            .Build();

        _slotStore = _host.Services.GetRequiredService<SlotConfigurationStore>();
        _providerRegistry = _host.Services.GetRequiredService<SlotProviderRegistry>();

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
    public async Task WhenRunCommandPublished_WorkflowLauncherIsCalledWithExtractedPath()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/my-workflow.zip",
            new Dictionary<string, string>());

        await _bus.SimulateReceivedAsync("workflow.run-commands", command);

        var launched = await _bus.WaitForConditionAsync(
            () => _launcher.Calls.Count > 0, Timeout);

        Assert.That(launched, Is.True, "Launcher was not called within the timeout.");
        Assert.That(_launcher.Calls[0].ExtractedContentDirectory, Does.Contain("auxilia-wf-"));
    }

    [Test]
    public async Task WhenRunCommandPublished_LauncherReceivesRabbitMqEnvVars()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "wf", "https://example.com/wf.zip", new Dictionary<string, string>());

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
            Guid.NewGuid(), "wf", "https://example.com/wf.zip",
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
    public async Task WhenSlotConfigSeeded_RegistrationSucceedsWithEmptySlots()
    {
        // Seed the store directly (mirrors what Program.cs does from appsettings)
        await _slotStore.UpsertConfigurationAsync("seeded-workflow",
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
        // Credentials no longer ship at registration — the slot activates just-in-time.
        Assert.That(response.Slots, Is.Empty);
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

    // ------------------------------------------------------------------ SlotPluginFiles component test

    // ------------------------------------------------------------------ Dispatch authorization

    [Test]
    public async Task WhenRunCommandCarriesPrincipalWithTriggerPermission_LaunchProceeds()
    {
        var directory = _host.Services.GetRequiredService<Auxilia.Governance.PrincipalDirectory>();
        var user = await directory.CreateHumanAsync("Triggerer", $"u-{Guid.NewGuid():N}", "pw");
        await directory.AssignRoleAsync(user.Id, Auxilia.Governance.BuiltInRoles.User);

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/my-workflow.zip",
            new Dictionary<string, string>(), RequestedBy: user.Id);

        await _bus.SimulateReceivedAsync("workflow.run-commands", command);

        var launched = await _bus.WaitForConditionAsync(() => _launcher.Calls.Count > 0, Timeout);
        Assert.That(launched, Is.True, "Authorized principal must be able to dispatch.");
    }

    [Test]
    public async Task WhenRunCommandCarriesPrincipalWithoutPermission_LaunchIsDeniedAndAudited()
    {
        var directory = _host.Services.GetRequiredService<Auxilia.Governance.PrincipalDirectory>();
        var nobody = await directory.CreateHumanAsync("No Roles", $"u-{Guid.NewGuid():N}", "pw");

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/my-workflow.zip",
            new Dictionary<string, string>(), RequestedBy: nobody.Id);

        await _bus.SimulateReceivedAsync("workflow.run-commands", command);

        var launched = await _bus.WaitForConditionAsync(
            () => _launcher.Calls.Count > 0, TimeSpan.FromMilliseconds(500));
        Assert.That(launched, Is.False, "Unauthorized principal must not dispatch.");

        var audit = _host.Services.GetRequiredService<
            Auxilia.UniversalDataAccess.IDataAccess<Auxilia.PlatformData.Entities.AuditRecord>>();
        var query = await audit.ReadAsync();
        Assert.That(query.Any(r => r.Action == "policy.denied" && r.Actor == nobody.Id.ToString()),
            Is.True, "The denial must be audited.");
    }

    [Test]
    public async Task WhenRunCommandPublished_AndSlotPackagesSeeded_LauncherReceivesSlotPluginFiles()
    {
        await _slotStore.UpsertConfigurationAsync("my-workflow",
            new StoredSlotConfiguration("slot1", "MyProvider",
                new Dictionary<string, string>(), ConfigurationStatus.Valid));
        await _providerRegistry.UpsertAsync("MyProvider", "/fake/path.slothandler.dll");

        var command = new RunWorkflowCommand(
            Guid.NewGuid(), "my-workflow", "https://example.com/my-workflow.zip",
            new Dictionary<string, string>());

        await _bus.SimulateReceivedAsync("workflow.run-commands", command);

        var launched = await _bus.WaitForConditionAsync(
            () => _launcher.Calls.Count > 0, Timeout);

        Assert.That(launched, Is.True, "Launcher was not called within the timeout.");
        Assert.That(_launcher.Calls[0].SlotPluginFiles, Has.Count.EqualTo(1));
        Assert.That(_launcher.Calls[0].SlotPluginFiles[0].DllPath, Is.EqualTo("/fake/path.slothandler.dll"));
    }
}
