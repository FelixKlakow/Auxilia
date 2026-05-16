using Auxilia.SteeringInstance.Workflows.Storage;

namespace Auxilia.SteeringInstance.Tests;

[TestFixture]
public class SlotConfigurationStoreTests
{
    private SlotConfigurationStore _store = null!;

    [SetUp]
    public void SetUp() => _store = new SlotConfigurationStore();

    [Test]
    public void UpsertConfiguration_AddsNewEntry()
    {
        var config = MakeConfig("slotA", ConfigurationStatus.Valid);
        _store.UpsertConfiguration("workflow1", config);

        var result = _store.GetConfigurations("workflow1");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].SlotName, Is.EqualTo("slotA"));
        Assert.That(result[0].Status, Is.EqualTo(ConfigurationStatus.Valid));
    }

    [Test]
    public void UpsertConfiguration_UpdatesExistingEntryBySlotName()
    {
        var original = MakeConfig("slotA", ConfigurationStatus.Valid);
        var updated = MakeConfig("slotA", ConfigurationStatus.Dirty);
        _store.UpsertConfiguration("workflow1", original);
        _store.UpsertConfiguration("workflow1", updated);

        var result = _store.GetConfigurations("workflow1");

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].Status, Is.EqualTo(ConfigurationStatus.Dirty));
    }

    [Test]
    public void MarkDirty_SetsAllEntriesForTypeToDirty()
    {
        _store.UpsertConfiguration("workflow1", MakeConfig("slotA", ConfigurationStatus.Valid));
        _store.UpsertConfiguration("workflow1", MakeConfig("slotB", ConfigurationStatus.Valid));

        _store.MarkDirty("workflow1");

        var result = _store.GetConfigurations("workflow1");
        Assert.That(result, Has.All.Property(nameof(StoredSlotConfiguration.Status))
            .EqualTo(ConfigurationStatus.Dirty));
    }

    [Test]
    public void MarkDirty_DoesNotAffectOtherWorkflowTypes()
    {
        _store.UpsertConfiguration("workflow1", MakeConfig("slotA", ConfigurationStatus.Valid));
        _store.UpsertConfiguration("workflow2", MakeConfig("slotB", ConfigurationStatus.Valid));

        _store.MarkDirty("workflow1");

        var workflow2Configs = _store.GetConfigurations("workflow2");
        Assert.That(workflow2Configs, Has.All.Property(nameof(StoredSlotConfiguration.Status))
            .EqualTo(ConfigurationStatus.Valid));
    }

    private static StoredSlotConfiguration MakeConfig(string slotName, ConfigurationStatus status)
        => new(slotName, "SomeProvider", new Dictionary<string, string>(), status);
}
