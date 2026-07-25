namespace Auxilia.Core.Contracts;

/// <summary>Visibility of a connector — who may bind it into a run.</summary>
public static class ConnectorScope
{
    /// <summary>Usable by any authenticated principal (shared team/company account).</summary>
    public const string Company = "Company";
    /// <summary>Identity-linked: usable only by the owner and principals a grant admits.</summary>
    public const string Personal = "Personal";
}

/// <summary>Kinds of subject a connector grant admits.</summary>
public static class ConnectorGrantKind
{
    /// <summary><see cref="ConnectorGrant.Id"/> is a principal id.</summary>
    public const string Principal = "Principal";
    /// <summary><see cref="ConnectorGrant.Id"/> is a directory (AD/Entra) group object id.</summary>
    public const string DirectoryGroup = "DirectoryGroup";
}

/// <summary>
/// Admits a subject to use a personal connector: a specific principal, or every principal whose
/// directory group membership includes the given AD group. Company connectors ignore grants.
/// </summary>
public sealed record ConnectorGrant(string Kind, string Id);

/// <summary>
/// Create a connector (a credential-bearing configuration instance). Settings are stored
/// encrypted at rest in the Core database and are never returned by any read endpoint. A
/// <see cref="ConnectorScope.Personal"/> connector is owned by the creating principal.
/// </summary>
public sealed record CreateConnector(
    string Name,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings,
    string Scope = ConnectorScope.Company);

/// <summary>Replace a connector's access grants (personal connectors only).</summary>
public sealed record SetConnectorGrants(IReadOnlyList<ConnectorGrant> Grants);

/// <summary>
/// A stored connector. Secret values are never included — only the setting keys that are
/// present, so a UI can show what is configured without exposing the material.
/// </summary>
public sealed record Connector(
    Guid Id,
    string Name,
    string ProviderType,
    IReadOnlyList<string> SettingKeys,
    DateTimeOffset UpdatedUtc,
    string Scope = ConnectorScope.Company,
    Guid? OwnerPrincipalId = null,
    IReadOnlyList<ConnectorGrant>? Grants = null);

/// <summary>Filter for querying connectors.</summary>
public sealed record ConnectorQuery(
    string? ProviderType = null,
    int Skip = 0,
    int Take = 50);
