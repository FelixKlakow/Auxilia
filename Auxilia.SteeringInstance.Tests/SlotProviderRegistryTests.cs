using Auxilia.SteeringInstance.Workflows.Storage;

namespace Auxilia.SteeringInstance.Tests;

[TestFixture]
public class SlotProviderRegistryTests
{
    private SlotProviderRegistry _registry = null!;

    [SetUp]
    public void SetUp() => _registry = TestStores.NewSlotProviderRegistry();

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
}
