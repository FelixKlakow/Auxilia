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
