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
}
