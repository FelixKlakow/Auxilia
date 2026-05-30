using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// <see cref="IServiceCollection"/> helpers for decorating registered AI agents with resilience.
/// </summary>
public static class AiAgentServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the keyed <see cref="IAiAgent"/> registration for <paramref name="slotName"/>
    /// with a <see cref="ResilientAiAgent"/> wrapper.
    /// </summary>
    /// <remarks>
    /// Call this inside <see cref="IWorkflowBuilder.ConfigureServices"/> after the provider slot
    /// handler has registered the inner agent (i.e., from inside the bootstrapped service collection).
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// No keyed <see cref="IAiAgent"/> registered under <paramref name="slotName"/> was found.
    /// </exception>
    public static IServiceCollection WrapAiAgentWithResilience(
        this IServiceCollection services,
        string slotName,
        AiResilienceOptions? options = null)
    {
        var descriptor = services.LastOrDefault(d =>
            d.IsKeyedService
            && string.Equals(d.ServiceKey as string, slotName, StringComparison.Ordinal)
            && d.ServiceType == typeof(IAiAgent));

        if (descriptor == null)
            throw new InvalidOperationException(
                $"No keyed IAiAgent with slot name '{slotName}' was found in the service collection. " +
                "Ensure the provider slot handler has registered the inner agent before calling WrapAiAgentWithResilience.");

        services.Remove(descriptor);
        var opts = options ?? new AiResilienceOptions();

        if (descriptor.KeyedImplementationInstance is IAiAgent instance)
        {
            services.AddKeyedSingleton<IAiAgent>(slotName, new ResilientAiAgent(instance, opts));
        }
        else if (descriptor.KeyedImplementationFactory != null)
        {
            var originalFactory = descriptor.KeyedImplementationFactory;
            services.AddKeyedSingleton<IAiAgent>(slotName,
                (sp, key) => new ResilientAiAgent((IAiAgent)originalFactory(sp, key), opts));
        }
        else
        {
            var implType = descriptor.KeyedImplementationType!;
            services.AddKeyedSingleton<IAiAgent>(slotName,
                (sp, key) => new ResilientAiAgent((IAiAgent)ActivatorUtilities.CreateInstance(sp, implType), opts));
        }

        return services;
    }
}
