using System.Security.Cryptography;
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
using Moq;

namespace Auxilia.SteeringInstance.Tests.ComponentTests;

/// <summary>
/// Component tests for the authenticated registration handshake (default mode):
/// the dispatcher issues a one-time instance token and pre-creates the response queue;
/// announcement and registration must present the token and are answered only on the
/// canonical per-instance queue.
/// </summary>
[TestFixture]
[Category("Component")]
public class AuthenticatedHandshakeComponentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private IHost _host = null!;
    private FakeMessageBusClient _bus = null!;
    private FakeWorkflowLauncher _launcher = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new FakeMessageBusClient();
        _launcher = new FakeWorkflowLauncher();

        var developerMode = new Mock<IDeveloperModeProvider>();
        developerMode.Setup(d => d.IsActive).Returns(true);

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMessageBusClient>(_bus);
                services.AddSingleton<IWorkflowLauncher>(_launcher);
                services.AddSingleton(new Mock<IHttpClientFactory>().Object);
                services.AddSingleton(developerMode.Object);
                services.AddSingleton<IWorkflowPackageVerifier, WorkflowPackageVerifier>();

                services.Configure<DockerWorkflowLauncherSettings>(s =>
                {
                    s.RabbitMqHost = "rabbitmq";
                });
                services.Configure<RunnerProfile>(p =>
                {
                    p.AvailableTools = new HashSet<string>();
                    p.OpenPorts = new HashSet<int>();
                });
                // RequireInstanceToken stays at its default (true).

                var platformData = new PlatformDataSettings { Backend = PlatformDataBackend.InMemory };
                services.AddPlatformEntity<WorkflowSchemaRecord>(platformData);
                services.AddPlatformEntity<SlotConfigurationRecord>(platformData);
                services.AddPlatformEntity<SlotProviderRecord>(platformData);
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
                services.AddSingleton<SlotProviderRegistry>();
                services.AddSingleton<SignalHandlerStore>();
                services.AddSingleton<WorkflowSchemaStore>();
                services.AddSingleton<PendingWorkflowPackageStore>();
                services.AddSingleton<WorkflowInstanceRegistry>();
                services.AddSingleton<EnvironmentValidator>();
                services.AddSingleton<ConfigurationResolver>();
                services.AddSingleton<WorkflowRegistrationHandler>();
                services.AddSingleton<WorkflowAnnouncementHandler>();
                services.AddSingleton<NetworkPolicyResolver>();
                services.AddSingleton<WorkspaceManager>();
                services.AddSingleton<WorkflowDispatcher>();
            })
            .Build();

        await _host.Services.GetRequiredService<WorkflowRegistrationHandler>().StartAsync(CancellationToken.None);
        await _host.Services.GetRequiredService<WorkflowAnnouncementHandler>().StartAsync(CancellationToken.None);
        await _host.Services.GetRequiredService<WorkflowDispatcher>().StartAsync(CancellationToken.None);
    }

    [TearDown]
    public void TearDown() => _host.Dispose();

    private static string AnyPublicKey()
    {
        using var rsa = RSA.Create(2048);
        return Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Dispatches a baked-image launch and returns the issued (instanceId, token).</summary>
    private async Task<(Guid InstanceId, string Token)> DispatchAsync(string workflowType = "auth-wf")
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), workflowType, "docker://auth-wf:test", new Dictionary<string, string>());

        await _bus.SimulateReceivedAsync("workflow.run-commands", command);
        await _bus.WaitForConditionAsync(() => _launcher.Calls.Count > 0, Timeout);

        var env = _launcher.Calls[0].EnvironmentVariables;
        return (Guid.Parse(env[WorkflowEnvironmentVariables.InstanceId]),
                env[WorkflowEnvironmentVariables.InstanceToken]);
    }

    [Test]
    public async Task FullHandshake_WithIssuedToken_SucceedsOnCanonicalQueue()
    {
        var (instanceId, token) = await DispatchAsync();
        var canonicalQueue = WorkflowQueues.ResponseQueueFor(instanceId);

        // Announce with the issued token — directive must arrive on the canonical queue.
        await _bus.SimulateReceivedAsync("workflow.announcements",
            new WorkflowAnnouncementMessage(instanceId, "auth-wf", AnyPublicKey(), "self-declared", token));

        var directiveSent = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is WorkflowDirective),
            Timeout);
        Assert.That(directiveSent, Is.True, "Run directive was not published to the canonical queue.");

        // Register (no slots) with the token — configuration must arrive on the canonical queue.
        await _bus.SimulateReceivedAsync("workflow-registration",
            new WorkflowRegistrationRequest(
                instanceId,
                new WorkflowManifest("auth-wf", instanceId.ToString(), [], [], string.Empty, [], []),
                AnyPublicKey(), "self-declared", token));

        var configSent = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is WorkflowConfigurationResponse),
            Timeout);
        Assert.That(configSent, Is.True, "Configuration response was not published to the canonical queue.");

        var response = (WorkflowConfigurationResponse)_bus.PublishedMessages
            .First(m => m.Topic == canonicalQueue && m.Message is WorkflowConfigurationResponse).Message;
        Assert.That(response.Success, Is.True);

        // Nothing must ever be delivered to the self-declared topic.
        Assert.That(_bus.PublishedMessages.Any(m => m.Topic == "self-declared"), Is.False);
    }

    [Test]
    public async Task Registration_WithoutToken_IsRejectedSilently()
    {
        var (instanceId, _) = await DispatchAsync();
        var canonicalQueue = WorkflowQueues.ResponseQueueFor(instanceId);

        await _bus.SimulateReceivedAsync("workflow-registration",
            new WorkflowRegistrationRequest(
                instanceId,
                new WorkflowManifest("auth-wf", instanceId.ToString(), [], [], string.Empty, [], []),
                AnyPublicKey(), "attacker-topic"));

        var responded = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m =>
                (m.Topic == canonicalQueue || m.Topic == "attacker-topic") &&
                m.Message is WorkflowConfigurationResponse),
            TimeSpan.FromMilliseconds(500));

        Assert.That(responded, Is.False, "A token-less registration must not receive any response.");
    }

    [Test]
    public async Task Announcement_WithForeignInstanceId_IsRejected()
    {
        await DispatchAsync();

        var foreignId = Guid.NewGuid();
        await _bus.SimulateReceivedAsync("workflow.announcements",
            new WorkflowAnnouncementMessage(foreignId, "auth-wf", AnyPublicKey(), "attacker-topic", "guessed-token"));

        var responded = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Message is WorkflowDirective),
            TimeSpan.FromMilliseconds(500));

        Assert.That(responded, Is.False, "An announcement with an unknown instance/token must be ignored.");
    }
}
