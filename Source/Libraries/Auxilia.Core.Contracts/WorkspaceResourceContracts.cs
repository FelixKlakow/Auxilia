namespace Auxilia.Core.Contracts;

/// <summary>
/// A first-class, Core-persisted repository/workspace resource: a workspace-mount provider's
/// NON-secret settings (clone URL, branch, working directory, cache/push policy, commit
/// identity, post-binding setup script — keyed by the provider's setting keys) plus an optional
/// credential connector reference. Configurations reference it by id
/// (<see cref="SlotBinding.WorkspaceId"/>), so editing the repository once applies to every
/// configuration using it — no per-configuration re-entry. Shared like connectors:
/// Personal (owner + grants) or Company.
/// </summary>
public sealed record WorkspaceResource(
    Guid Id,
    string Name,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings,
    Guid? ConnectorId,
    DateTimeOffset UpdatedUtc,
    string Scope = ResourceScope.Personal,
    Guid? OwnerPrincipalId = null,
    IReadOnlyList<AccessGrant>? Grants = null);

/// <summary>Creates a repository resource; a Company-scoped one requires the manage permission.</summary>
public sealed record CreateWorkspaceResource(
    string Name,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings,
    Guid? ConnectorId = null,
    string Scope = ResourceScope.Personal);

/// <summary>Full replacement of a repository's name, settings, and connector reference.</summary>
public sealed record UpdateWorkspaceResource(
    string Name,
    IReadOnlyDictionary<string, string> Settings,
    Guid? ConnectorId = null);

/// <summary>Replaces a personal repository's access grants (owner, or a manager).</summary>
public sealed record SetWorkspaceGrants(IReadOnlyList<AccessGrant> Grants);
