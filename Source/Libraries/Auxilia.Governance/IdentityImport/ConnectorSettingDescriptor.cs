namespace Auxilia.Governance.IdentityImport;

/// <summary>How an identity-import connector setting is edited and rendered by admin UIs.</summary>
public enum ConnectorSettingKind
{
    Text,
    Secret,
    Number,
    Boolean
}

/// <summary>
/// Machine-readable description of one identity-import connector setting so admin UIs can
/// render real forms. Deliberately a minimal sibling of the workflow SDK's
/// <c>Auxilia.Workflows.SettingDescriptor</c>: Governance must not reference the workflow SDK
/// (which carries AI and messaging dependencies), so the two stay separate types.
/// </summary>
public sealed record ConnectorSettingDescriptor(
    string Key,
    string Label,
    ConnectorSettingKind Kind,
    bool Required = false,
    string? HelpText = null,
    string? DefaultValue = null,
    bool Multiline = false);
