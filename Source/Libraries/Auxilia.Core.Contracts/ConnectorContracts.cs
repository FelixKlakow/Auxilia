namespace Auxilia.Core.Contracts;

/// <summary>
/// Create a connector (a credential-bearing configuration instance). Settings are stored
/// encrypted at rest in the Core database and are never returned by any read endpoint. A
/// <see cref="ResourceScope.Personal"/> connector is owned by the creating principal.
/// Sharing platform-wide (<see cref="ResourceScope.Company"/>) is the deliberate opt-in.
/// </summary>
public sealed record CreateConnector(
    string Name,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings,
    string Scope = ResourceScope.Personal);

/// <summary>
/// Update a connector in place: a null field stays unchanged; provided <see cref="Settings"/>
/// are upserted key-by-key (existing keys keep their stored value) — the way to refresh a
/// rotated credential without re-creating the connector or touching its bindings.
/// </summary>
public sealed record UpdateConnector(
    string? Name = null,
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>Replace a connector's access grants (personal connectors only).</summary>
public sealed record SetConnectorGrants(IReadOnlyList<AccessGrant> Grants);

/// <summary>
/// Ask the Core to browse live data reachable with a connector's credential (the secret never
/// leaves the Core). <see cref="Kind"/> names WHAT to list — the vocabulary comes from provider
/// setting descriptors (their <c>Browse</c> metadata), not from this contract. <see cref="Context"/>
/// optionally scopes the browse (e.g. list the branches OF one repository); editors fill it from
/// the sibling setting the descriptor's <c>BrowseDependsOn</c> names. Callers must be eligible to
/// use the connector (the same gate as dispatch).
/// </summary>
public sealed record BrowseConnector(string Kind, string? Context = null);

/// <summary>One browsable item (id = machine value, label = display).</summary>
public sealed record ConnectorBrowseItem(string Id, string Label);

/// <summary>The result of browsing with a connector's credential.</summary>
public sealed record ConnectorBrowseResult(IReadOnlyList<ConnectorBrowseItem> Items);

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
    string Scope = ResourceScope.Personal,
    Guid? OwnerPrincipalId = null,
    IReadOnlyList<AccessGrant>? Grants = null);

/// <summary>Filter for querying connectors.</summary>
public sealed record ConnectorQuery(
    string? ProviderType = null,
    int Skip = 0,
    int Take = 50);
