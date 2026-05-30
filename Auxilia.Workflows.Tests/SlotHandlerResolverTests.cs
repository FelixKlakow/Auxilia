using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class SlotHandlerResolverTests
{
    [Test]
    public void Resolve_WithRegisteredHandler_ReturnsHandler()
    {
        var resolver = new SlotHandlerResolver();
        var handler = new NoOpSlotHandler();
        resolver.Register("provider-a", handler);

        var resolved = resolver.Resolve("provider-a");

        Assert.That(resolved, Is.SameAs(handler));
    }

    [Test]
    public void Resolve_WithUnknownProviderType_ThrowsKeyNotFoundException()
    {
        var resolver = new SlotHandlerResolver();

        Assert.Throws<KeyNotFoundException>(() => resolver.Resolve("not-registered-ever-xyz"));
    }

    [Test]
    public void TwoInstances_DoNotShareState()
    {
        var resolverA = new SlotHandlerResolver();
        resolverA.Register("provider-b", new NoOpSlotHandler());

        var resolverB = new SlotHandlerResolver();

        Assert.Throws<KeyNotFoundException>(() => resolverB.Resolve("provider-b"));
    }

    private sealed class NoOpSlotHandler : ISlotHandler
    {
        public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration) { }
    }
}
