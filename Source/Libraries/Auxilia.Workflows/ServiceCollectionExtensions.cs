using Auxilia.Workflows.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Workflows;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSlotHandlerResolver(this IServiceCollection services)
        => services.AddScoped<ISlotHandlerResolver, SlotHandlerResolver>();

    public static IServiceCollection AddPluginSecurity(this IServiceCollection services)
    {
        services.AddSingleton<IDeveloperModeProvider, EnvironmentDeveloperModeProvider>();
        services.AddSingleton<IPluginManifestVerifier, PluginManifestVerifier>();
        return services;
    }

    public static IServiceCollection AddPluginDiscovery(this IServiceCollection services)
    {
        services.AddSingleton<IPluginDiscovery, FileSystemPluginDiscovery>();
        return services;
    }
}
