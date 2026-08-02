namespace Auxilia.Core.Contracts;

/// <summary>
/// Create or update an identity source (bulk/offline user provisioning from LDAP/AD or CSV).
/// Set <see cref="ExistingName"/> to the current name when editing; leave it null to create.
/// Secret settings are write-only — send an empty value to keep the stored secret unchanged.
/// </summary>
public sealed record SaveIdentitySourceRequest(
    string Name,
    string ConnectorType,
    IReadOnlyDictionary<string, string> Settings,
    string DefaultRole,
    IReadOnlyDictionary<string, string> GroupRoleMappings,
    bool DisableMissing,
    string? ExistingName = null);

/// <summary>One configured identity source as shown to admins — secret setting values are never returned.</summary>
public sealed record IdentitySourceDto(
    Guid Id,
    string Name,
    string ConnectorType,
    bool DisableMissing,
    string DefaultRole,
    IReadOnlyDictionary<string, string> GroupRoleMappings,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<string> StoredSecretKeys,
    IdentityImportSummaryDto? LastImport,
    DateTimeOffset? LastImportUtc);

/// <summary>Result counts of one import run (an idempotent upsert; missing users are at most disabled).</summary>
public sealed record IdentityImportSummaryDto(
    int Created, int Updated, int Disabled, int Skipped, IReadOnlyList<string> Warnings);

/// <summary>Whether a source is reachable and how many users it would deliver (never leaks secrets).</summary>
public sealed record IdentityConnectorTestResult(bool Success, string Message);

/// <summary>An available import connector type and the settings it accepts, so admin UIs can render forms.</summary>
public sealed record IdentityConnectorDescriptorDto(
    string ConnectorType,
    string DisplayName,
    string Description,
    IReadOnlyList<IdentityConnectorSettingDto> Settings);

/// <summary>One connector setting: how it is edited and whether it is required.</summary>
public sealed record IdentityConnectorSettingDto(
    string Key,
    string Label,
    string Kind,
    bool Required,
    string? HelpText,
    string? DefaultValue,
    bool Multiline);
