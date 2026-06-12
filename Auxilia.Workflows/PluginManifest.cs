namespace Auxilia.Workflows;

public sealed record PluginManifest(
    string ProviderType,
    string ContentHashBase64,
    string SignatureBase64,
    string PublicKeyBase64,
    IReadOnlyList<SettingDescriptor>? Settings = null);
