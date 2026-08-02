namespace Auxilia.Core.Contracts;

/// <summary>
/// Visibility of a shareable resource (connector, run configuration) — who may use it.
/// </summary>
public static class ResourceScope
{
    /// <summary>Usable by any authenticated principal (shared team/company resource).</summary>
    public const string Company = "Company";
    /// <summary>Identity-linked: usable only by the owner and subjects a grant admits.</summary>
    public const string Personal = "Personal";
}

/// <summary>Kinds of subject an access grant admits.</summary>
public static class AccessGrantKind
{
    /// <summary><see cref="AccessGrant.Id"/> is a principal id.</summary>
    public const string Principal = "Principal";
    /// <summary><see cref="AccessGrant.Id"/> is a first-class platform group id.</summary>
    public const string Group = "Group";
    /// <summary><see cref="AccessGrant.Id"/> is a directory (AD/Entra) group object id.</summary>
    public const string DirectoryGroup = "DirectoryGroup";
}

/// <summary>
/// Admits a subject to use a personal resource (connector, run configuration): a specific
/// principal, every member of a first-class platform group, or every principal whose directory
/// group membership includes the given AD group. Company-scoped resources ignore grants.
/// </summary>
public sealed record AccessGrant(string Kind, string Id);

/// <summary>A built-in role and the permission actions it grants, as served by <c>GET /api/roles</c>.</summary>
public sealed record RoleDto(string Name, IReadOnlyList<string> Permissions);

/// <summary>A principal as seen by sharing pickers — id and display name only, no admin detail.</summary>
public sealed record SharingPrincipal(Guid Id, string DisplayName, string Kind);

/// <summary>A first-class platform group as seen by sharing pickers.</summary>
public sealed record SharingGroup(Guid Id, string Name);

/// <summary>
/// The subjects a grant editor can pick from, served by <c>GET /api/directory/subjects</c> to
/// ANY authenticated principal — sharing needs a people/group picker, not principal
/// administration, so only ids and display names are exposed.
/// </summary>
public sealed record SharingSubjects(
    IReadOnlyList<SharingPrincipal> Principals,
    IReadOnlyList<SharingGroup> Groups);
