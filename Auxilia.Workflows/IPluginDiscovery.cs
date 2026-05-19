namespace Auxilia.Workflows;

public interface IPluginDiscovery
{
    IReadOnlyList<DiscoveredPlugin> DiscoverPlugins(string pluginDirectory);
}
