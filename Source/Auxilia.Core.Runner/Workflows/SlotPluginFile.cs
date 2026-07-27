namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Identifies a slot handler plugin DLL and its co-located manifest file.
/// </summary>
public sealed record SlotPluginFile(
    string DllPath, string ManifestPath,
    /// <summary>Dependency DLLs shipped beside the plugin (manifest opt-in: BundleDependencies).</summary>
    IReadOnlyList<string>? DependencyPaths = null);
