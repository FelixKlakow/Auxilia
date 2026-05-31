using Auxilia.SteeringInstance.Workflows.Storage;

namespace Auxilia.SteeringInstance.Tests;

[TestFixture]
public class SlotProviderRegistryTests
{
    private SlotProviderRegistry _registry = null!;

    [SetUp]
    public void SetUp() => _registry = new SlotProviderRegistry();

    [Test]
    public void Upsert_ThenTryGet_ReturnsDllPath()
    {
        _registry.Upsert("MyProvider", "/fake/path.slothandler.dll");

        var found = _registry.TryGet("MyProvider", out var dllPath);

        Assert.That(found, Is.True);
        Assert.That(dllPath, Is.EqualTo("/fake/path.slothandler.dll"));
    }

    [Test]
    public void Remove_ThenTryGet_ReturnsFalse()
    {
        _registry.Upsert("MyProvider", "/fake/path.slothandler.dll");
        _registry.Remove("MyProvider");

        var found = _registry.TryGet("MyProvider", out _);

        Assert.That(found, Is.False);
    }

    [Test]
    public void Upsert_Twice_OverwritesPreviousValue()
    {
        _registry.Upsert("MyProvider", "/original/path.slothandler.dll");
        _registry.Upsert("MyProvider", "/updated/path.slothandler.dll");

        _registry.TryGet("MyProvider", out var dllPath);

        Assert.That(dllPath, Is.EqualTo("/updated/path.slothandler.dll"));
    }
}
