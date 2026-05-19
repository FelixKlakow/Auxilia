using System.Text.Json;

namespace Auxilia.Workflows;

public sealed class FileSystemPluginDiscovery : IPluginDiscovery
{
    public IReadOnlyList<DiscoveredPlugin> DiscoverPlugins(string pluginDirectory)
    {
        if (!Directory.Exists(pluginDirectory))
            return [];

        var results = new List<DiscoveredPlugin>();

        foreach (var dllPath in Directory.EnumerateFiles(pluginDirectory, "*.slothandler.dll"))
        {
            var manifestPath = Path.ChangeExtension(dllPath, null) + ".manifest.json";

            if (!File.Exists(manifestPath))
                continue;

            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson);

            if (manifest is null)
                continue;

            results.Add(new DiscoveredPlugin(manifest.ProviderType, dllPath, manifest));
        }

        return results;
    }
}
