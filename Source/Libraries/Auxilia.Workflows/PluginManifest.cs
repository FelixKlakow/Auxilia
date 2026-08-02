namespace Auxilia.Workflows;

public sealed record PluginManifest(
    string ProviderType,
    string ContentHashBase64,
    string SignatureBase64,
    string PublicKeyBase64,
    IReadOnlyList<SettingDescriptor>? Settings = null)
{
    /// <summary>
    /// Full names of the capability contracts this provider can back (e.g.
    /// <c>Auxilia.Workflows.TaskSource.IWorkItemAccess</c>). Configuration tooling offers the
    /// provider only for slots declaring a matching contract; empty means unclassified.
    /// </summary>
    public IReadOnlyList<string>? Contracts { get; init; }

    /// <summary>Human slot-kind tag (e.g. "task-source"); admins may override it in the catalog.</summary>
    public string? Category { get; init; }

    /// <summary>One plain-language sentence describing what the provider does, shown to admins and configurators.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Ship the plugin's NuGet dependency closure (every sibling non-<c>Auxilia.*</c> DLL) into
    /// the container beside the plugin. For plugins with package dependencies the image does not
    /// carry (SDK clients etc.); shared Auxilia contracts always come from the image.
    /// </summary>
    public bool BundleDependencies { get; init; }

    /// <summary>
    /// Auxilia-prefixed sibling DLLs to bundle DESPITE the shared-contracts-from-the-image rule —
    /// a plugin's OWN Auxilia assemblies (e.g. its adapter) that no workflow image carries.
    /// Only meaningful with <see cref="BundleDependencies"/>.
    /// </summary>
    public IReadOnlyList<string>? BundledAuxiliaAssemblies { get; init; }
}
