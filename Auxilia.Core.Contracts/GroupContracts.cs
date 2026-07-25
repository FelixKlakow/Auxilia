namespace Auxilia.Core.Contracts;

/// <summary>Create a first-class group.</summary>
public sealed record CreateGroupRequest(string Name, string? Description = null);

/// <summary>A first-class group with its members and granted roles.</summary>
public sealed record GroupDto(
    Guid Id, string Name, string? Description, IReadOnlyList<Guid> Members, IReadOnlyList<string> Roles);

/// <summary>Add a principal to a group.</summary>
public sealed record AddGroupMemberRequest(Guid PrincipalId);

/// <summary>Grant a built-in role to every member of a group.</summary>
public sealed record AssignGroupRoleRequest(string RoleName);
