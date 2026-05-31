namespace Auxilia.SteeringInstance.Workflows;

/// <summary>
/// Identifies a slot handler plugin DLL and its co-located manifest file.
/// </summary>
public sealed record SlotPluginFile(string DllPath, string ManifestPath);
