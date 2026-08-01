using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance;

/// <summary>
/// Resolves the roles a principal holds through first-class group memberships. The Policy Engine
/// unions these with the principal's direct role assignments.
/// </summary>
public sealed class GroupRoleResolver(
    IDataAccess<GroupMembershipRecord> memberships,
    IDataAccess<GroupRoleRecord> groupRoles)
{
    /// <summary>The first-class groups the principal is a member of.</summary>
    public async Task<IReadOnlyList<Guid>> GroupsForAsync(Guid principalId, CancellationToken ct = default)
        => (await memberships.ReadAsync(ct))
            .Where(m => m.PrincipalId == principalId)
            .Select(m => m.GroupId)
            .Distinct()
            .ToList();

    public async Task<IReadOnlyList<string>> RolesForAsync(Guid principalId, CancellationToken ct = default)
    {
        var membershipQuery = await memberships.ReadAsync(ct);
        var groupIds = membershipQuery
            .Where(m => m.PrincipalId == principalId)
            .Select(m => m.GroupId)
            .ToHashSet();
        if (groupIds.Count == 0)
            return [];

        var roleQuery = await groupRoles.ReadAsync(ct);
        return roleQuery
            .Where(gr => groupIds.Contains(gr.GroupId))
            .Select(gr => gr.RoleName)
            .Distinct()
            .ToList();
    }
}
