using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// One first-class repository/workspace resource: a workspace-mount provider's non-secret
/// settings plus an optional credential-connector reference, shared like connectors
/// (Personal owner + grants, or Company). Bindings reference it by id; dispatch expands the
/// reference live, so edits apply to every configuration using it.
/// </summary>
public sealed record CoreWorkspaceRecord : IEntity
{
    public Guid Id { get; init; }

    public required string Name { get; init; }

    public required string ProviderType { get; init; }

    /// <summary>Plain (non-secret) settings JSON, keyed by the provider's setting keys.</summary>
    public string SettingsJson { get; init; } = "{}";

    /// <summary>The credential connector the mount authenticates with, when any.</summary>
    public Guid? ConnectorId { get; init; }

    public string Scope { get; init; } = ResourceScope.Personal;

    public Guid? OwnerPrincipalId { get; init; }

    public string GrantsJson { get; init; } = "[]";

    public DateTimeOffset UpdatedUtc { get; init; }
}
