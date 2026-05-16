using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Tests;

[TestFixture]
public class SlotHandlerRegistryTests
{
    [Test]
    public void Resolve_WithRegisteredHandler_ReturnsHandler()
    {
        var providerType = $"registry-test-{Guid.NewGuid()}";
        var handler = new NoOpSlotHandler();
        SlotHandlerRegistry.Register(providerType, handler);

        var resolved = SlotHandlerRegistry.Resolve(providerType);

        Assert.That(resolved, Is.SameAs(handler));
    }

    [Test]
    public void Resolve_WithUnknownProviderType_ThrowsKeyNotFoundException()
    {
        Assert.Throws<KeyNotFoundException>(() => SlotHandlerRegistry.Resolve("not-registered-ever-xyz"));
    }

    private sealed class NoOpSlotHandler : ISlotHandler
    {
        public void Register(IServiceCollection services, SlotConfiguration configuration) { }
    }
}
