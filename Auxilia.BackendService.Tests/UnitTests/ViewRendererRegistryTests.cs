using Auxilia.BackendService.Components;
using Auxilia.BackendService.Dashboard;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class ViewRendererRegistryTests
{
    [Test]
    public void Resolve_RegisteredKey_ReturnsComponentType()
    {
        var registry = new ViewRendererRegistry(
            [new ViewRendererRegistration("agent-chat", typeof(AgentChatRenderer))]);

        Assert.That(registry.Resolve("agent-chat"), Is.EqualTo(typeof(AgentChatRenderer)));
    }

    [Test]
    public void Resolve_UnknownKey_ReturnsNull()
    {
        var registry = new ViewRendererRegistry(
            [new ViewRendererRegistration("agent-chat", typeof(AgentChatRenderer))]);

        Assert.That(registry.Resolve("scatter-plot"), Is.Null);
    }

    [Test]
    public void Resolve_NullKey_ReturnsNull()
    {
        var registry = new ViewRendererRegistry([]);

        Assert.That(registry.Resolve(null), Is.Null);
    }

    [Test]
    public void AddViewRenderer_RegistersKeyResolvableThroughDiBuiltRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ViewRendererRegistry>();
        services.AddViewRenderer<AgentChatRenderer>("agent-chat");

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<ViewRendererRegistry>();

        Assert.That(registry.Resolve("agent-chat"), Is.EqualTo(typeof(AgentChatRenderer)));
    }

    [Test]
    public void AddViewRenderer_EmptyKey_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new ServiceCollection().AddViewRenderer<AgentChatRenderer>(""));
    }
}
