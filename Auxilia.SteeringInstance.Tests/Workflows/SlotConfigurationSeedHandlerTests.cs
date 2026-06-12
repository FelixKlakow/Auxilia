using Auxilia.SteeringInstance.Tests.ComponentTests;
using Auxilia.SteeringInstance.Workflows;
using Auxilia.SteeringInstance.Workflows.Storage;
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
    private SlotProviderRegistry _providerRegistry = null!;
    private SlotConfigurationSeedHandler _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _fakeBus = new FakeMessageBusClient();
        _slotStore = new SlotConfigurationStore();
        _providerRegistry = new SlotProviderRegistry();
        _sut = new SlotConfigurationSeedHandler(
            _fakeBus,
            _slotStore,
            _providerRegistry,
            Options.Create(new WorkflowDispatcherSettings { CommandQueueName = "test-queue" }),
            NullLogger<SlotConfigurationSeedHandler>.Instance);

        await _sut.StartAsync(CancellationToken.None);
    }

    [Test]
    public async Task WhenUpsertCommandReceived_SlotIsStoredWithValidStatus()
    {
        var cmd = new UpsertSlotConfigurationCommand(
            "implementation-workflow", "repository", "fake-provider",
            new Dictionary<string, string> { ["key"] = "value" });

        await _fakeBus.SimulateReceivedAsync("slot-configurations", cmd);

        var configs = _slotStore.GetConfigurations("implementation-workflow");
        Assert.That(configs, Has.Count.EqualTo(1));
        Assert.That(configs[0].SlotName, Is.EqualTo("repository"));
        Assert.That(configs[0].ProviderType, Is.EqualTo("fake-provider"));
        Assert.That(configs[0].Status, Is.EqualTo(ConfigurationStatus.Valid));
    }

    [Test]
    public async Task WhenRemoveCommandReceived_SlotIsRemovedFromStore()
    {
        _slotStore.UpsertConfiguration("implementation-workflow",
            new StoredSlotConfiguration("repository", "fake-provider", new Dictionary<string, string>(), ConfigurationStatus.Valid));

        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new RemoveSlotConfigurationCommand("implementation-workflow", "repository"));

        var configs = _slotStore.GetConfigurations("implementation-workflow");
        Assert.That(configs, Is.Empty);
    }

    [Test]
    public async Task WhenRegisterProviderCommandReceived_ProviderIsRegistered()
    {
        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new RegisterSlotProviderCommand("fake-provider", "/fake/path.slothandler.dll"));

        var found = _providerRegistry.TryGet("fake-provider", out var dllPath);
        Assert.That(found, Is.True);
        Assert.That(dllPath, Is.EqualTo("/fake/path.slothandler.dll"));
    }

    [Test]
    public async Task WhenRemoveProviderCommandReceived_ProviderIsRemoved()
    {
        _providerRegistry.Upsert("fake-provider", "/fake/path.slothandler.dll");

        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new RemoveSlotProviderCommand("fake-provider"));

        var found = _providerRegistry.TryGet("fake-provider", out _);
        Assert.That(found, Is.False);
    }

    [Test]
    public async Task WhenUpsertCommandReceivedForDifferentSlot_DirtySlotRemainsUnchanged()
    {
        _slotStore.UpsertConfiguration("implementation-workflow",
            new StoredSlotConfiguration("dirty-slot", "some-provider", new Dictionary<string, string>(), ConfigurationStatus.Valid));
        _slotStore.MarkDirty("implementation-workflow");

        await _fakeBus.SimulateReceivedAsync("slot-configurations",
            new UpsertSlotConfigurationCommand(
                "implementation-workflow", "other-slot", "other-provider",
                new Dictionary<string, string>()));

        var configs = _slotStore.GetConfigurations("implementation-workflow");
        var dirtySlot = configs.First(c => c.SlotName == "dirty-slot");
        Assert.That(dirtySlot.Status, Is.EqualTo(ConfigurationStatus.Dirty));
    }

    [Test]
    public async Task WhenSeedQueueMessageReceived_UpsertCommandIsApplied()
    {
        await _fakeBus.SimulateReceivedAsync("test-queue-slot-seed.upsert",
            new UpsertSlotConfigurationCommand("wf", "slot-a", "provider-x", new Dictionary<string, string>()));

        var configs = _slotStore.GetConfigurations("wf");
        Assert.That(configs, Has.Count.EqualTo(1));
        Assert.That(configs[0].SlotName, Is.EqualTo("slot-a"));
        Assert.That(configs[0].Status, Is.EqualTo(ConfigurationStatus.Valid));
    }
}
