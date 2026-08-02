namespace Auxilia.Workflows;

public sealed record DiscoveredPlugin(
    string ProviderType,
    string AssemblyPath,
    PluginManifest Manifest);
