using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// One configured identity-import source: an external system (LDAP directory, CSV upload, ...)
/// administrators import users from. Connector settings are stored protected; imported
/// principals authenticate only after an administrator sets local credentials (v1 — no
/// pass-through authentication against the source).
/// </summary>
public sealed record IdentitySourceRecord : IEntity
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Connector type key, e.g. "ldap" or "csv".</summary>
    public required string ConnectorType { get; init; }
    /// <summary>Connector settings as protected JSON (run through ISettingsProtector).</summary>
    public string ProtectedSettingsJson { get; init; } = "";
    /// <summary>Role every imported user receives; empty = none.</summary>
    public string DefaultRole { get; init; } = "";
    /// <summary>JSON object mapping external group name → role name.</summary>
    public string GroupRoleMappingsJson { get; init; } = "{}";
    /// <summary>Disable principals that disappeared from the source on the next import; imports never delete.</summary>
    public bool DisableMissing { get; init; }
    public string? LastImportSummaryJson { get; init; }
    public DateTimeOffset? LastImportUtc { get; init; }

    public static Guid IdFor(string name) => DeterministicGuid.For("identity-source", name);
}
