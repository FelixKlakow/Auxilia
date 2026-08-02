using System.Reflection;

namespace Auxilia.Workflows;

public sealed class PluginLoader(
    ISlotHandlerResolver resolver,
    IPluginManifestVerifier verifier,
    Func<string, Assembly>? assemblyLoader = null)
{
    private readonly Func<string, Assembly> _assemblyLoader = assemblyLoader ?? Assembly.LoadFrom;

    public void Load(IReadOnlyList<DiscoveredPlugin> plugins)
    {
        foreach (var plugin in plugins)
        {
            var bytes = File.ReadAllBytes(plugin.AssemblyPath);

            if (!verifier.Verify(plugin.Manifest, bytes))
                throw new PluginVerificationException(plugin.ProviderType);

            var assembly = _assemblyLoader(plugin.AssemblyPath);

            var handlerTypes = assembly.GetTypes()
                .Where(t => typeof(ISlotHandler).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
                .ToList();

            if (handlerTypes.Count == 0)
                throw new InvalidOperationException(
                    $"Assembly '{plugin.AssemblyPath}' contains no concrete ISlotHandler implementation.");

            if (handlerTypes.Count > 1)
                throw new InvalidOperationException(
                    $"Assembly '{plugin.AssemblyPath}' contains {handlerTypes.Count} concrete ISlotHandler implementations; exactly one is required.");

            var handler = (ISlotHandler)Activator.CreateInstance(handlerTypes[0])!;
            resolver.Register(plugin.ProviderType, handler);
        }
    }
}
