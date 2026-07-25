using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// A credential-bearing connector instance. Setting values are stored protected (encrypted at
/// rest) and only ever leave the Core as just-in-time injected credentials — never through a
/// read endpoint (Principle 3: secrets live only in the Core).
/// </summary>
public sealed record CoreConnectorRecord : IEntity
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string ProviderType { get; init; }

    /// <summary>JSON dictionary of setting key → protected value.</summary>
    public string ProtectedSettingsJson { get; init; } = "{}";

    /// <summary>"Company" (shared) or "Personal" (identity-linked, owner + granted subjects only).</summary>
    public string Scope { get; init; } = Core.Contracts.ConnectorScope.Company;

    /// <summary>The principal who owns a personal connector; null for company connectors.</summary>
    public Guid? OwnerPrincipalId { get; init; }

    /// <summary>JSON array of <c>ConnectorGrant</c> admitting subjects to a personal connector.</summary>
    public string GrantsJson { get; init; } = "[]";

    public DateTimeOffset UpdatedUtc { get; init; }
}
