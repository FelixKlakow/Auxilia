using Auxilia.Workflows.AiAgent;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.SlotPackages.Tests.AiAgent;

[TestFixture]
[Category("Unit")]
public class AiAgentServiceCollectionExtensionsTests
{
    private sealed class FakeAiAgent : IAiAgent
    {
        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    [Test]
    public void WrapAiAgentWithResilience_InstanceRegistration_ReplacesWithResilientDecorator()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IAiAgent>("my-agent", new FakeAiAgent());

        services.WrapAiAgentWithResilience("my-agent");

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredKeyedService<IAiAgent>("my-agent");
        Assert.That(resolved, Is.InstanceOf<ResilientAiAgent>());
    }

    [Test]
    public void WrapAiAgentWithResilience_FactoryRegistration_ReplacesWithResilientDecorator()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IAiAgent>("my-agent", (_, _) => new FakeAiAgent());

        services.WrapAiAgentWithResilience("my-agent", new AiResilienceOptions { MaxAttempts = 5 });

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredKeyedService<IAiAgent>("my-agent");
        Assert.That(resolved, Is.InstanceOf<ResilientAiAgent>());
    }

    [Test]
    public void WrapAiAgentWithResilience_SlotNotRegistered_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            services.WrapAiAgentWithResilience("nonexistent"));
    }

    [Test]
    public void WrapAiAgentWithResilience_TypeRegistration_ReplacesWithResilientDecorator()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IAiAgent, FakeAiAgent>("my-agent");

        services.WrapAiAgentWithResilience("my-agent");

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredKeyedService<IAiAgent>("my-agent");
        Assert.That(resolved, Is.InstanceOf<ResilientAiAgent>());
    }

    [Test]
    public void WrapAiAgentWithResilience_OnlyOneDescriptorRemains_OriginalRemoved()
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IAiAgent>("my-agent", new FakeAiAgent());

        services.WrapAiAgentWithResilience("my-agent");

        var count = services.Count(d =>
            d.IsKeyedService
            && string.Equals(d.ServiceKey as string, "my-agent", StringComparison.Ordinal)
            && d.ServiceType == typeof(IAiAgent));
        Assert.That(count, Is.EqualTo(1), "Exactly one IAiAgent keyed descriptor should remain.");
    }
}
