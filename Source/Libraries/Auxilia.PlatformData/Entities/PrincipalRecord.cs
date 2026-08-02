using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>An authenticated identity: human user, AI agent, or service principal.</summary>
public sealed record PrincipalRecord : IEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid TenantId { get; init; }
    /// <summary>"Human", "AiAgent", or "Service".</summary>
    public required string Kind { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>Subject identifier at an external identity provider; null for local principals.</summary>
    public string? ExternalSubject { get; init; }
    /// <summary>"Active" or "Disabled".</summary>
    public required string Status { get; init; }
    /// <summary>
    /// JSON array of the principal's directory (AD/Entra) group object ids, refreshed from the token
    /// at each federated sign-in. Drives AD-group-gated connector access; empty for local principals.
    /// </summary>
    public string DirectoryGroupsJson { get; init; } = "[]";
}
