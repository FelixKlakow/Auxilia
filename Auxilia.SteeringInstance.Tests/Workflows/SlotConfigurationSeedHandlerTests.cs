using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.SteeringInstance.Tests.ComponentTests;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.SteeringInstance.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class SlotConfigurationSeedHandlerTests
{
    private FakeMessageBusClient _fakeBus = null!;
    private SlotConfigurationStore _slotStore = null!;
    private InMemoryDataAccess<SlotProviderRecord> _providerRecords = null!;
    private SlotProviderRegistry _providerRegistry = null!;
    private WorkflowConfigurationStore _configurationStore = null!;
    private SlotConfigurationSeedHandler _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fakeBus = new FakeMessageBusClient();
        _slotStore = TestStores.NewSlotConfigurationStore();
        _providerRecords = new InMemoryDataAccess<SlotProviderRecord>();
        _providerRegistry = new SlotProviderRegistry(_providerRecords);
        _configurationStore = TestStores.NewWorkflowConfigurationStore();
        _sut = new SlotConfigurationSeedHandler(
            _fakeBus,
            _slotStore,
            _providerRegistry,
            _configurationStore,
            TestStores.NewDrainCoordinator(_fakeBus),
            Options.Create(new WorkflowDispatcherSettings { CommandQueueName = "test-queue" }),
            NullLogger<SlotConfigurationSeedHandler>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public void TearDown() => _providerRecords.Dispose();

    [Test]
    public async Task WhenUpsertCommandReceived_SlotIsStoredWithValidStatus()
    {
        var cmd = new UpsertSlotConfigurationCommand(
            "implementation-workflow", "repository", "fake-provider",
            new Dictionary<string, string> { ["key"] = "value" });

        await _fakeBus.SimulateReceivedAsync("slot-configurations", cmd);

        var configs = await _slotStore.GetConfigurationsAsync("implementation-workflow");
        Assert.That(configs, Has.Count.EqualTo(1));
        Assert.That(configs[0].SlotName, Is.EqualTo("repository"));
        Assert.That(configs[0].ProviderType, Is.EqualTo("fake-provider"));
        Assert.That(configs[0].Status, Is.EqualTo(ConfigurationStatus.Valid));
    }

    [Test]
    public async Task WhenRemoveCommandReceived_SlotIsRemovedFromStore()
    {
        await _slotStore.UpsertConfigurationAsync("implementation-workflow",
            new StoredSlotConfiguration("repository", "fake-provider", new Dictionary<string, string>(), ConfigurationStatus.Valid));

        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new RemoveSlotConfigurationCommand("implementation-workflow", "repository"));

        var configs = await _slotStore.GetConfigurationsAsync("implementation-workflow");
        Assert.That(configs, Is.Empty);
    }

    [Test]
    public async Task WhenRegisterProviderCommandReceived_ProviderIsRegistered()
    {
        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new RegisterSlotProviderCommand("fake-provider", "/fake/path.slothandler.dll"));

        var dllPath = await _providerRegistry.GetDllPathAsync("fake-provider");
        Assert.That(dllPath, Is.EqualTo("/fake/path.slothandler.dll"));
    }

    [Test]
    public async Task WhenRegisterProviderCommandCarriesSettingDescriptors_TheyLandOnTheRecord()
    {
        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new RegisterSlotProviderCommand("email-work-items", "/plugins/email.slothandler.dll",
            [
                new SettingDescriptor("ImapHost", "IMAP host", SettingKind.Text, Required: true),
                new SettingDescriptor("Password", "Password", SettingKind.Secret, Required: true)
            ]));

        var record = (await _providerRecords.ReadAsync(SlotProviderRecord.IdFor("email-work-items")))!;
        var descriptors = JsonSerializer.Deserialize<List<SettingDescriptor>>(record.SettingDescriptorsJson!)!;
        Assert.Multiple(() =>
        {
            Assert.That(descriptors.Select(d => d.Key), Is.EqualTo(new[] { "ImapHost", "Password" }));
            Assert.That(descriptors[1].Kind, Is.EqualTo(SettingKind.Secret));
        });
    }

    [Test]
    public async Task WhenRegisterProviderCommandCarriesNoDescriptors_RecordDescriptorsStayNull()
    {
        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new RegisterSlotProviderCommand("legacy-provider", "/plugins/legacy.slothandler.dll"));

        var record = (await _providerRecords.ReadAsync(SlotProviderRecord.IdFor("legacy-provider")))!;
        Assert.That(record.SettingDescriptorsJson, Is.Null);
    }

    [Test]
    public async Task WhenRemoveProviderCommandReceived_ProviderIsRemoved()
    {
        await _providerRegistry.UpsertAsync("fake-provider", "/fake/path.slothandler.dll");

        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new RemoveSlotProviderCommand("fake-provider"));

        var dllPath = await _providerRegistry.GetDllPathAsync("fake-provider");
        Assert.That(dllPath, Is.Null);
    }

    [Test]
    public async Task WhenUpsertCommandReceivedForDifferentSlot_DirtySlotRemainsUnchanged()
    {
        await _slotStore.UpsertConfigurationAsync("implementation-workflow",
            new StoredSlotConfiguration("dirty-slot", "some-provider", new Dictionary<string, string>(), ConfigurationStatus.Valid));
        await _slotStore.MarkDirtyAsync("implementation-workflow");

        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new UpsertSlotConfigurationCommand(
                "implementation-workflow", "other-slot", "other-provider",
                new Dictionary<string, string>()));

        var configs = await _slotStore.GetConfigurationsAsync("implementation-workflow");
        var dirtySlot = configs.First(c => c.SlotName == "dirty-slot");
        Assert.That(dirtySlot.Status, Is.EqualTo(ConfigurationStatus.Dirty));
    }

    [Test]
    public async Task WhenSeedQueueMessageReceived_UpsertCommandIsApplied()
    {
        await _fakeBus.SimulateReceivedAsync("test-queue-slot-seed.upsert",
            new UpsertSlotConfigurationCommand("wf", "slot-a", "provider-x", new Dictionary<string, string>()));

        var configs = await _slotStore.GetConfigurationsAsync("wf");
        Assert.That(configs, Has.Count.EqualTo(1));
        Assert.That(configs[0].SlotName, Is.EqualTo("slot-a"));
        Assert.That(configs[0].Status, Is.EqualTo(ConfigurationStatus.Valid));
    }

    // ---------------------------------------------------- Named workflow configurations (#18)

    [Test]
    public async Task WhenUpsertWorkflowConfigurationReceivedOnExchange_ConfigurationIsStored()
    {
        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new UpsertWorkflowConfigurationCommand(
                "alpha", "Alpha", "wf", "docker://wf:test", Enabled: true,
                [new SlotBindingSeed("repo", "provider-x",
                    new Dictionary<string, string> { ["Url"] = "https://example.com" })]));

        var stored = await _configurationStore.GetByNameAsync("alpha");
        Assert.That(stored, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stored!.WorkflowType, Is.EqualTo("wf"));
            Assert.That(stored.PackageUri, Is.EqualTo("docker://wf:test"));
            Assert.That(stored.Enabled, Is.True);
            Assert.That(stored.SlotBindings, Has.Count.EqualTo(1));
            Assert.That(stored.SlotBindings[0].Settings["Url"], Is.EqualTo("https://example.com"));
        });
    }

    [Test]
    public async Task WhenUpsertWorkflowConfigurationReceivedOnSeedQueue_ConfigurationIsStored()
    {
        await _fakeBus.SimulateReceivedAsync("test-queue-slot-seed.upsert-configuration",
            new UpsertWorkflowConfigurationCommand(
                "beta", "Beta", "wf", "docker://wf:test", Enabled: true,
                [new SlotBindingSeed("repo", "provider-x", new Dictionary<string, string>())]));

        var stored = await _configurationStore.GetByNameAsync("beta");
        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.DisplayName, Is.EqualTo("Beta"));
    }

    [Test]
    public async Task WhenUpsertWorkflowConfigurationIsInvalid_NothingIsStored()
    {
        await _fakeBus.SimulateReceivedAsync("test-queue-slot-seed.upsert-configuration",
            new UpsertWorkflowConfigurationCommand(
                "gamma", "Gamma", "", "docker://wf:test", Enabled: true, []));

        Assert.That(await _configurationStore.GetByNameAsync("gamma"), Is.Null);
    }

    [Test]
    public async Task WhenRemoveWorkflowConfigurationReceivedOnExchange_ConfigurationIsRemoved()
    {
        await _configurationStore.UpsertAsync(new StoredWorkflowConfiguration(
            "delta", "Delta", "wf", "docker://wf:test", Enabled: true, []));

        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new RemoveWorkflowConfigurationCommand("delta"));

        Assert.That(await _configurationStore.GetByNameAsync("delta"), Is.Null);
    }

    [Test]
    public async Task WhenRemoveWorkflowConfigurationReceivedOnSeedQueue_ConfigurationIsRemoved()
    {
        await _configurationStore.UpsertAsync(new StoredWorkflowConfiguration(
            "epsilon", "Epsilon", "wf", "docker://wf:test", Enabled: true, []));

        await _fakeBus.SimulateReceivedAsync("test-queue-slot-seed.remove-configuration",
            new RemoveWorkflowConfigurationCommand("epsilon"));

        Assert.That(await _configurationStore.GetByNameAsync("epsilon"), Is.Null);
    }

    [Test]
    public async Task WhenRemoveWorkflowConfigurationTargetsUnknownName_NothingHappens()
    {
        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new RemoveWorkflowConfigurationCommand("does-not-exist"));

        Assert.That(await _configurationStore.GetByNameAsync("does-not-exist"), Is.Null);
    }
}
