using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Governance;
using Auxilia.Messaging;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.Core.Runner.Workflows;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.UniversalDataAccess;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Auxilia.Core.Runner.Tests.ComponentTests;

/// <summary>
/// Component tests for named-configuration dispatch (#18) over the in-memory bus: a
/// configuration is seeded via the per-instance seed queue, a run is dispatched by
/// configuration ID only, the instance record carries the configuration ID/name, and
/// just-in-time slot activation serves the binding's settings.
/// </summary>
[TestFixture]
[Category("Component")]
public class WorkflowConfigurationDispatchComponentTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const string ConfigurationName = "alpha";
    private const string SeedQueue = "workflow.run-commands-slot-seed.upsert-configuration";
    private const string RegisterProviderQueue = "workflow.run-commands-slot-seed.register";
    private const string UpsertInstanceQueue = "workflow.run-commands-slot-seed.upsert-instance";

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
                services.AddPlatformEntity<SlotInstanceRecord>(platformData);
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
                services.AddSingleton<SlotInstanceStore>();
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
                services.AddSingleton<SlotConfigurationSeedHandler>();
                services.AddSingleton<LongLivingDrainCoordinator>();
                services.AddSingleton<NetworkPolicyResolver>();
                services.AddSingleton<WorkspaceManager>();
                services.AddSingleton(TestStores.NewArtifactStore());
                services.AddSingleton<WorkflowDispatcher>();
            })
            .Build();

        await _host.Services.GetRequiredService<SlotConfigurationSeedHandler>().StartAsync(CancellationToken.None);
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

    /// <summary>Seeds the provider and the named configuration over the seed queue family.</summary>
    private async Task SeedConfigurationAsync()
    {
        await _bus.SimulateReceivedAsync(RegisterProviderQueue,
            new RegisterSlotProviderCommand("cfg-provider", "/plugins/cfg-provider.slothandler.dll"));
        await _bus.SimulateReceivedAsync(SeedQueue,
            new UpsertWorkflowConfigurationCommand(
                ConfigurationName, "Alpha", "cfg-wf", "docker://cfg-wf:test", Enabled: true,
                [new SlotBindingSeed("repo", "cfg-provider",
                    new Dictionary<string, string> { ["RepositoryUrl"] = "https://alpha.example/repo.git" })]));
    }

    /// <summary>Dispatches by configuration ID only and returns the issued (instanceId, token).</summary>
    private async Task<(Guid InstanceId, string Token)> DispatchByConfigurationAsync()
    {
        var command = new RunWorkflowCommand(
            Guid.NewGuid(), null, null, new Dictionary<string, string>(),
            WorkflowConfigurationId: WorkflowConfigurationRecord.IdFor(ConfigurationName));

        await _bus.SimulateReceivedAsync("workflow.run-commands", command);
        var launched = await _bus.WaitForConditionAsync(() => _launcher.Calls.Count > 0, Timeout);
        Assert.That(launched, Is.True, "Launcher was not called within the timeout.");

        var env = _launcher.Calls[0].EnvironmentVariables;
        return (Guid.Parse(env[WorkflowEnvironmentVariables.InstanceId]),
                env[WorkflowEnvironmentVariables.InstanceToken]);
    }

    /// <summary>Runs the authenticated handshake for a manifest with one slot ("repo").</summary>
    private async Task<(Guid InstanceId, string Token)> HandshakeAsync()
    {
        var (instanceId, token) = await DispatchByConfigurationAsync();
        var canonicalQueue = WorkflowQueues.ResponseQueueFor(instanceId);

        await _bus.SimulateReceivedAsync("workflow.announcements",
            new WorkflowAnnouncementMessage(instanceId, "cfg-wf", AnyPublicKey(), "self-declared", token));
        await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is WorkflowDirective),
            Timeout);

        await _bus.SimulateReceivedAsync("workflow-registration",
            new WorkflowRegistrationRequest(
                instanceId,
                new WorkflowManifest("cfg-wf", instanceId.ToString(),
                    [new SlotDefinition("repo", null) { ServiceType = typeof(object) }],
                    [], string.Empty, [], []),
                AnyPublicKey(), "self-declared", token));
        await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is WorkflowConfigurationResponse),
            Timeout);

        return (instanceId, token);
    }

    [Test]
    public async Task RegisterProvider_WithSettingDescriptors_LandsThemOnTheProviderRecord()
    {
        await _bus.SimulateReceivedAsync(RegisterProviderQueue,
            new RegisterSlotProviderCommand("email-work-items", "/plugins/email.slothandler.dll",
            [
                new SettingDescriptor("ImapHost", "IMAP host", SettingKind.Text, Required: true),
                new SettingDescriptor("Password", "Password", SettingKind.Secret, Required: true,
                    HelpText: "Stored encrypted at rest.")
            ]));

        var records = _host.Services.GetRequiredService<IDataAccess<SlotProviderRecord>>();
        var record = await records.ReadAsync(SlotProviderRecord.IdFor("email-work-items"));
        Assert.That(record, Is.Not.Null);
        var descriptors = JsonSerializer.Deserialize<List<SettingDescriptor>>(record!.SettingDescriptorsJson!)!;
        Assert.Multiple(() =>
        {
            Assert.That(descriptors.Select(d => d.Key), Is.EqualTo(new[] { "ImapHost", "Password" }));
            Assert.That(descriptors[1].Kind, Is.EqualTo(SettingKind.Secret));
            Assert.That(descriptors[1].HelpText, Is.EqualTo("Stored encrypted at rest."));
        });
    }

    [Test]
    public async Task DispatchByConfigurationId_LaunchesConfiguredImage_AndRecordsConfigurationOnInstance()
    {
        await SeedConfigurationAsync();

        var (instanceId, _) = await DispatchByConfigurationAsync();

        Assert.That(_launcher.Calls[0].DockerImageUri, Is.EqualTo("cfg-wf:test"));
        Assert.That(_launcher.Calls[0].SlotPluginFiles.Select(f => f.DllPath),
            Is.EqualTo(new[] { "/plugins/cfg-provider.slothandler.dll" }));

        var records = _host.Services.GetRequiredService<IDataAccess<WorkflowInstanceRecord>>();
        var record = await records.ReadAsync(instanceId);
        Assert.That(record, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(record!.WorkflowType, Is.EqualTo("cfg-wf"));
            Assert.That(record.WorkflowConfigurationId,
                Is.EqualTo(WorkflowConfigurationRecord.IdFor(ConfigurationName)));
            Assert.That(record.WorkflowConfigurationName, Is.EqualTo(ConfigurationName));
        });
    }

    [Test]
    public async Task DispatchByConfigurationId_RegistrationSucceeds_AndSlotActivationServesBindingSettings()
    {
        await SeedConfigurationAsync();
        var (instanceId, token) = await HandshakeAsync();
        var canonicalQueue = WorkflowQueues.ResponseQueueFor(instanceId);

        var registration = (WorkflowConfigurationResponse)_bus.PublishedMessages
            .First(m => m.Topic == canonicalQueue && m.Message is WorkflowConfigurationResponse).Message;
        Assert.That(registration.Success, Is.True,
            $"Registration of a configuration-bound instance must succeed: {registration.ErrorMessage}");

        using var rsa = RSA.Create(4096);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        await _bus.SimulateReceivedAsync("workflow-slot-activation",
            new SlotActivationRequest(instanceId, "repo", publicKey, token));

        var responded = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is SlotActivationResponse),
            Timeout);
        Assert.That(responded, Is.True, "Slot activation response was not published.");

        var response = (SlotActivationResponse)_bus.PublishedMessages
            .First(m => m.Topic == canonicalQueue && m.Message is SlotActivationResponse).Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True);
            Assert.That(response.Slot!.ProviderType, Is.EqualTo("cfg-provider"));
        });

        var plainJson = System.Text.Encoding.UTF8.GetString(rsa.Decrypt(
            Convert.FromBase64String(response.Slot!.EncryptedSettings), RSAEncryptionPadding.OaepSHA256));
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson);
        Assert.That(settings!["RepositoryUrl"], Is.EqualTo("https://alpha.example/repo.git"),
            "Slot activation must serve the configuration binding's settings.");
    }

    [Test]
    public async Task InstanceBackedBinding_SlotActivationServesTheInstanceSettings()
    {
        // A reusable slot instance plus a configuration whose binding references it by ID.
        await _bus.SimulateReceivedAsync(RegisterProviderQueue,
            new RegisterSlotProviderCommand("cfg-provider", "/plugins/cfg-provider.slothandler.dll"));
        await _bus.SimulateReceivedAsync(UpsertInstanceQueue,
            new UpsertSlotInstanceCommand(
                "shared-repo", "Shared repo", "cfg-provider",
                new Dictionary<string, string> { ["RepositoryUrl"] = "https://shared.example/repo.git" }));
        await _bus.SimulateReceivedAsync(SeedQueue,
            new UpsertWorkflowConfigurationCommand(
                ConfigurationName, "Alpha", "cfg-wf", "docker://cfg-wf:test", Enabled: true,
                [new SlotBindingSeed("repo", "", new Dictionary<string, string>(),
                    SlotInstanceRecord.IdFor("shared-repo"))]));

        var (instanceId, token) = await HandshakeAsync();
        var canonicalQueue = WorkflowQueues.ResponseQueueFor(instanceId);

        using var rsa = RSA.Create(4096);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        await _bus.SimulateReceivedAsync("workflow-slot-activation",
            new SlotActivationRequest(instanceId, "repo", publicKey, token));
        var responded = await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is SlotActivationResponse),
            Timeout);
        Assert.That(responded, Is.True, "Slot activation response was not published.");

        var response = (SlotActivationResponse)_bus.PublishedMessages
            .First(m => m.Topic == canonicalQueue && m.Message is SlotActivationResponse).Message;
        Assert.Multiple(() =>
        {
            Assert.That(response.Success, Is.True, response.ErrorMessage);
            Assert.That(response.Slot!.ProviderType, Is.EqualTo("cfg-provider"),
                "the provider comes from the referenced instance");
        });

        var plainJson = System.Text.Encoding.UTF8.GetString(rsa.Decrypt(
            Convert.FromBase64String(response.Slot!.EncryptedSettings), RSAEncryptionPadding.OaepSHA256));
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(plainJson);
        Assert.That(settings!["RepositoryUrl"], Is.EqualTo("https://shared.example/repo.git"),
            "Slot activation must serve the referenced instance's settings.");
    }

    [Test]
    public async Task DeletedInstanceReference_FailsRegistrationPreFlight()
    {
        await _bus.SimulateReceivedAsync(RegisterProviderQueue,
            new RegisterSlotProviderCommand("cfg-provider", "/plugins/cfg-provider.slothandler.dll"));
        await _bus.SimulateReceivedAsync(UpsertInstanceQueue,
            new UpsertSlotInstanceCommand(
                "shared-repo", "Shared repo", "cfg-provider", new Dictionary<string, string>()));
        await _bus.SimulateReceivedAsync(SeedQueue,
            new UpsertWorkflowConfigurationCommand(
                ConfigurationName, "Alpha", "cfg-wf", "docker://cfg-wf:test", Enabled: true,
                [new SlotBindingSeed("repo", "", new Dictionary<string, string>(),
                    SlotInstanceRecord.IdFor("shared-repo"))]));
        await _bus.SimulateReceivedAsync("workflow.run-commands-slot-seed.remove-instance",
            new RemoveSlotInstanceCommand("shared-repo"));

        var (instanceId, token) = await DispatchByConfigurationAsync();
        var canonicalQueue = WorkflowQueues.ResponseQueueFor(instanceId);
        await _bus.SimulateReceivedAsync("workflow.announcements",
            new WorkflowAnnouncementMessage(instanceId, "cfg-wf", AnyPublicKey(), "self-declared", token));
        await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is WorkflowDirective),
            Timeout);
        await _bus.SimulateReceivedAsync("workflow-registration",
            new WorkflowRegistrationRequest(
                instanceId,
                new WorkflowManifest("cfg-wf", instanceId.ToString(),
                    [new SlotDefinition("repo", null) { ServiceType = typeof(object) }],
                    [], string.Empty, [], []),
                AnyPublicKey(), "self-declared", token));
        await _bus.WaitForConditionAsync(
            () => _bus.PublishedMessages.Any(m => m.Topic == canonicalQueue && m.Message is WorkflowConfigurationResponse),
            Timeout);

        var registration = (WorkflowConfigurationResponse)_bus.PublishedMessages
            .First(m => m.Topic == canonicalQueue && m.Message is WorkflowConfigurationResponse).Message;
        Assert.Multiple(() =>
        {
            Assert.That(registration.Success, Is.False);
            Assert.That(registration.ErrorMessage, Does.Contain("deleted slot instance"));
        });
    }
}
