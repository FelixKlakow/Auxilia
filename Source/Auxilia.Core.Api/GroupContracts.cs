namespace Auxilia.Core.Api;

public sealed record CreateGroupRequest(string Name, string? Description = null);

public sealed record GroupDto(
    Guid Id, string Name, string? Description, IReadOnlyList<Guid> Members, IReadOnlyList<string> Roles);

public sealed record AddGroupMemberRequest(Guid PrincipalId);

public sealed record AssignGroupRoleRequest(string RoleName);
