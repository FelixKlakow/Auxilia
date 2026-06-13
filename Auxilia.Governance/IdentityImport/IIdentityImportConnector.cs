namespace Auxilia.Governance.IdentityImport;

/// <summary>One user as reported by an external identity system.</summary>
public sealed record ExternalUser(
    string ExternalId,
    string DisplayName,
    string Username,
    bool Enabled,
    IReadOnlyList<string> Groups);

/// <summary>Fetched users plus human-readable reasons for source entries that could not be mapped.</summary>
public sealed record IdentityImportFetch(
    IReadOnlyList<ExternalUser> Users,
    IReadOnlyList<string> SkippedEntries);

public sealed record ConnectorTestResult(bool Success, string Message);

/// <summary>
/// Reads users from one kind of external identity system (LDAP/Active Directory, CSV, ...).
/// Settings arrive as an already-decrypted plain dictionary — storage protects them via
/// <c>ISettingsProtector</c>; connectors must never log or echo setting values.
/// </summary>
public interface IIdentityImportConnector
{
    /// <summary>Stable type key stored in <c>IdentitySourceRecord.ConnectorType</c>.</summary>
    string ConnectorType { get; }

    string DisplayName { get; }

    string Description { get; }

    IReadOnlyList<ConnectorSettingDescriptor> SettingDescriptors { get; }

    /// <summary>Verifies the source is reachable with the given settings and reports how many users it would deliver.</summary>
    Task<ConnectorTestResult> TestConnectionAsync(
        IReadOnlyDictionary<string, string> settings, CancellationToken ct = default);

    Task<IdentityImportFetch> FetchUsersAsync(
        IReadOnlyDictionary<string, string> settings, CancellationToken ct = default);
}
