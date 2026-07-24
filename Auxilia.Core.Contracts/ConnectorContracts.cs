namespace Auxilia.Core.Contracts;

/// <summary>
/// Create a connector (a credential-bearing configuration instance). Settings are stored
/// encrypted at rest in the Core database and are never returned by any read endpoint.
/// </summary>
public sealed record CreateConnector(
    string Name,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// A stored connector. Secret values are never included — only the setting keys that are
/// present, so a UI can show what is configured without exposing the material.
/// </summary>
public sealed record Connector(
    Guid Id,
    string Name,
    string ProviderType,
    IReadOnlyList<string> SettingKeys,
    DateTimeOffset UpdatedUtc);

/// <summary>Filter for querying connectors.</summary>
public sealed record ConnectorQuery(
    string? ProviderType = null,
    int Skip = 0,
    int Take = 50);
