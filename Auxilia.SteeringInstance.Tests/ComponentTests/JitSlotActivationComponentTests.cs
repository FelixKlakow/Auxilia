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
using Moq;

namespace Auxilia.SteeringInstance.Tests.ComponentTests;

/// <summary>
/// Component tests for just-in-time per-slot credential delivery: registration of a slotted
/// workflow succeeds without shipping any credentials; the workflow then activates each slot
/// on demand with its instance token and receives the configuration encrypted for its
/// ephemeral key on the canonical per-instance queue.
/// </summary>
[TestFixture]
[Category("Component")]
public class JitSlotActivationComponentTests
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
                services.AddSingleton<EnvironmentValidator>();
                services.AddSingleton<DirtyConfigurationDetector>();
                services.AddSingleton<ConfigurationResolver>();
                services.AddSingleton<WorkflowRegistrationHandler>();
                services.AddSingleton<WorkflowAnnouncementHandler>();
                services.AddSingleton<SlotActivationHandler>();
                services.AddSingleton<NetworkPolicyResolver>();
                services.AddSingleton<WorkspaceManager>();
                services.AddSingleton<WorkflowDispatcher>();
            })
            .Build();

        await _host.Services.GetRequiredService<WorkflowRegistrationHandler>().StartAsync(CancellationToken.None);
        await _host.Services.GetRequiredService<WorkflowAnnouncementHandler>().StartAsync(CancellationToken.None);
        await _host.Services.GetRequiredService<SlotActivationHandler>().StartAsync(CancellationToken.None);
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
    private async Task<(Guid InstanceId, string Token)> DispatchAsync(string workflowType = "jit-wf")
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), workflowType, "docker://jit-wf:test", new Dictionary<string, string>());

        await _bus.SimulateReceivedAsync("workflow.run-commands", command);
        await _bus.WaitForConditionAsync(() => _launcher.Calls.Count > 0, Timeout);

        var env = _launcher.Calls[0].EnvironmentVariables;
        return (Guid.Parse(env[WorkflowEnvironmentVariables.InstanceId]),
                env[WorkflowEnvironmentVariables.InstanceToken]);
    }

    /// <summary>Runs the full authenticated handshake for a manifest with one slot ("repo").</summary>
    private async Task<(Guid InstanceId, string Token)> HandshakeWithSlotAsync()
    {
        await _host.Services.GetRequiredService<SlotConfigurationStore>()
            .UpsertConfigurationAsync("jit-wf", new StoredSlotConfiguration(
                "repo", "git",
                new Dictionary<string, string> { ["RepositoryUrl"] = "https://example.com/repo.git" },
                ConfigurationStatus.Valid));

        var (instanceId, token) = await DispatchAsync();
        var canonicalQueue = WorkflowQueues.ResponseQueueFor(instanceId);

        await _bus.SimulateReceivedAsync("workflow.announcements",
            new WorkflowAnnouncementMessage(instanceId, "jit-wf", AnyPublicKey(), "self-declared", token));
        await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is WorkflowDirective),
            Timeout);

        await _bus.SimulateReceivedAsync("workflow-registration",
            new WorkflowRegistrationRequest(
                instanceId,
                new WorkflowManifest("jit-wf", instanceId.ToString(),
                    [new SlotDefinition("repo", null) { ServiceType = typeof(object) }],
                    [], string.Empty, [], []),
                AnyPublicKey(), "self-declared", token));
        await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is WorkflowConfigurationResponse),
            Timeout);

        return (instanceId, token);
    }

    [Test]
    public async Task SlottedRegistration_Succeeds_WithoutShippingCredentials()
    {
        var (instanceId, _) = await HandshakeWithSlotAsync();
        var canonicalQueue = WorkflowQueues.ResponseQueueFor(instanceId);

        var response = (WorkflowConfigurationResponse)_bus.PublishedMessages
            .First(m => m.Topic == canonicalQueue && m.Message is WorkflowConfigurationResponse).Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.Slots, Is.Empty,
                "Credentials must not ship at registration — slots activate just-in-time.");
        });
    }

    [Test]
    public async Task SlotActivation_WithIssuedToken_DeliversDecryptableSlotOnCanonicalQueue()
    {
        var (instanceId, token) = await HandshakeWithSlotAsync();
        var canonicalQueue = WorkflowQueues.ResponseQueueFor(instanceId);

        using var rsa = RSA.Create(4096);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        await _bus.SimulateReceivedAsync("workflow-slot-activation",
            new SlotActivationRequest(instanceId, "repo", publicKey, token));

        var responded = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is SlotActivationResponse),
            Timeout);
        Assert.That(responded, Is.True, "Slot activation response was not published to the canonical queue.");

        var response = (SlotActivationResponse)_bus.PublishedMessages
            .First(m => m.Topic == canonicalQueue && m.Message is SlotActivationResponse).Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.SlotName, Is.EqualTo("repo"));
            Assert.That(response.Slot, Is.Not.Null);
            Assert.That(response.Slot!.ProviderType, Is.EqualTo("git"));
            Assert.That(response.ExpiresUtc, Is.Not.Null);
        });

        // The settings must be decryptable with the requester's private key.
        var cipherBytes = Convert.FromBase64String(response.Slot!.EncryptedSettings);
        var plainJson = System.Text.Encoding.UTF8.GetString(
            rsa.Decrypt(cipherBytes, RSAEncryptionPadding.OaepSHA256));
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson);
        Assert.That(settings, Is.Not.Null);
        Assert.That(settings!["RepositoryUrl"], Is.EqualTo("https://example.com/repo.git"));
    }

    [Test]
    public async Task SlotActivation_WithWrongToken_ReceivesNoResponse()
    {
        var (instanceId, _) = await HandshakeWithSlotAsync();
        var canonicalQueue = WorkflowQueues.ResponseQueueFor(instanceId);

        await _bus.SimulateReceivedAsync("workflow-slot-activation",
            new SlotActivationRequest(instanceId, "repo", AnyPublicKey(), "guessed-token"));

        var responded = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Message is SlotActivationResponse),
            TimeSpan.FromMilliseconds(500));

        Assert.That(responded, Is.False,
            "A slot activation with an invalid token must be rejected silently.");
        Assert.That(_bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is SlotActivationResponse),
            Is.False);
    }
}
