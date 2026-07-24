using Auxilia.Core.Runner.Workflows.Storage;

namespace Auxilia.Core.Runner.Tests;

[TestFixture]
public class SlotConfigurationStoreTests
{
    private SlotConfigurationStore _store = null!;

    [SetUp]
    public void SetUp() => _store = TestStores.NewSlotConfigurationStore();

    [Test]
    public async Task UpsertConfiguration_AddsNewEntry()
    {
        var config = MakeConfig("slotA", ConfigurationStatus.Valid);
        await _store.UpsertConfigurationAsync("workflow1", config);

        var result = await _store.GetConfigurationsAsync("workflow1");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].SlotName, Is.EqualTo("slotA"));
        Assert.That(result[0].Status, Is.EqualTo(ConfigurationStatus.Valid));
    }

    [Test]
    public async Task UpsertConfiguration_UpdatesExistingEntryBySlotName()
    {
        var original = MakeConfig("slotA", ConfigurationStatus.Valid);
        var updated = MakeConfig("slotA", ConfigurationStatus.Dirty);
        await _store.UpsertConfigurationAsync("workflow1", original);
        await _store.UpsertConfigurationAsync("workflow1", updated);

        var result = await _store.GetConfigurationsAsync("workflow1");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Status, Is.EqualTo(ConfigurationStatus.Dirty));
    }

    [Test]
    public async Task RemoveConfiguration_RemovesEntry()
    {
        await _store.UpsertConfigurationAsync("workflow1", MakeConfig("slotA", ConfigurationStatus.Valid));

        await _store.RemoveConfigurationAsync("workflow1", "slotA");

        var result = await _store.GetConfigurationsAsync("workflow1");
        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task MarkDirty_SetsAllEntriesForTypeToDirty()
    {
        await _store.UpsertConfigurationAsync("workflow1", MakeConfig("slotA", ConfigurationStatus.Valid));
        await _store.UpsertConfigurationAsync("workflow1", MakeConfig("slotB", ConfigurationStatus.Valid));

        await _store.MarkDirtyAsync("workflow1");

        var result = await _store.GetConfigurationsAsync("workflow1");
        Assert.That(result, Has.All.Property(nameof(StoredSlotConfiguration.Status))
            .EqualTo(ConfigurationStatus.Dirty));
    }

    [Test]
    public async Task MarkDirty_DoesNotAffectOtherWorkflowTypes()
    {
        await _store.UpsertConfigurationAsync("workflow1", MakeConfig("slotA", ConfigurationStatus.Valid));
        await _store.UpsertConfigurationAsync("workflow2", MakeConfig("slotB", ConfigurationStatus.Valid));

        await _store.MarkDirtyAsync("workflow1");

        var workflow2Configs = await _store.GetConfigurationsAsync("workflow2");
        Assert.That(workflow2Configs, Has.All.Property(nameof(StoredSlotConfiguration.Status))
            .EqualTo(ConfigurationStatus.Valid));
    }

    private static StoredSlotConfiguration MakeConfig(string slotName, ConfigurationStatus status)
        => new(slotName, "SomeProvider", new Dictionary<string, string>(), status);
}
