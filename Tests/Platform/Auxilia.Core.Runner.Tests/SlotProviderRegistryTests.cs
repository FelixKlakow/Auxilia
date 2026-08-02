using System.Text.Json;
using Auxilia.PlatformData.Entities;
using Auxilia.Core.Runner.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows;

namespace Auxilia.Core.Runner.Tests;

[TestFixture]
[Category("Unit")]
public class SlotProviderRegistryTests
{
    private InMemoryDataAccess<SlotProviderRecord> _records = null!;
    private SlotProviderRegistry _registry = null!;

    [SetUp]
    public void SetUp()
    {
        _records = new InMemoryDataAccess<SlotProviderRecord>();
        _registry = new SlotProviderRegistry(_records);
    }

    [TearDown]
    public void TearDown() => _records.Dispose();

    [Test]
    public async Task Upsert_ThenGet_ReturnsDllPath()
    {
        await _registry.UpsertAsync("MyProvider", "/fake/path.slothandler.dll");

        var dllPath = await _registry.GetDllPathAsync("MyProvider");

        Assert.That(dllPath, Is.EqualTo("/fake/path.slothandler.dll"));
    }

    [Test]
    public async Task Remove_ThenGet_ReturnsNull()
    {
        await _registry.UpsertAsync("MyProvider", "/fake/path.slothandler.dll");
        await _registry.RemoveAsync("MyProvider");

        var dllPath = await _registry.GetDllPathAsync("MyProvider");

        Assert.That(dllPath, Is.Null);
    }

    [Test]
    public async Task Upsert_Twice_OverwritesPreviousValue()
    {
        await _registry.UpsertAsync("MyProvider", "/original/path.slothandler.dll");
        await _registry.UpsertAsync("MyProvider", "/updated/path.slothandler.dll");

        var dllPath = await _registry.GetDllPathAsync("MyProvider");

        Assert.That(dllPath, Is.EqualTo("/updated/path.slothandler.dll"));
    }

    [Test]
    public async Task Upsert_WithSettingDescriptors_PersistsThemAsJsonOnTheRecord()
    {
        await _registry.UpsertAsync("MyProvider", "/fake/path.slothandler.dll",
            [new SettingDescriptor("Password", "Password", SettingKind.Secret, Required: true)]);

        var record = (await _records.ReadAsync(SlotProviderRecord.IdFor("MyProvider")))!;
        var descriptors = JsonSerializer.Deserialize<List<SettingDescriptor>>(record.SettingDescriptorsJson!)!;
        Assert.Multiple(() =>
        {
            Assert.That(descriptors, Has.Count.EqualTo(1));
            Assert.That(descriptors[0].Key, Is.EqualTo("Password"));
            Assert.That(descriptors[0].Kind, Is.EqualTo(SettingKind.Secret));
            Assert.That(descriptors[0].Required, Is.True);
        });
    }

    [Test]
    public async Task Upsert_WithoutSettingDescriptors_LeavesRecordDescriptorsNull()
    {
        await _registry.UpsertAsync("MyProvider", "/fake/path.slothandler.dll");

        var record = (await _records.ReadAsync(SlotProviderRecord.IdFor("MyProvider")))!;
        Assert.That(record.SettingDescriptorsJson, Is.Null);
    }

    [Test]
    public async Task Upsert_WithEmptyDescriptorList_LeavesRecordDescriptorsNull()
    {
        await _registry.UpsertAsync("MyProvider", "/fake/path.slothandler.dll", []);

        var record = (await _records.ReadAsync(SlotProviderRecord.IdFor("MyProvider")))!;
        Assert.That(record.SettingDescriptorsJson, Is.Null);
    }
}
