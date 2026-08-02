using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance;

/// <summary>
/// Administration of first-class groups: creation, membership, and group role grants. Every
/// mutating operation is audited. Roles a principal gains through a group are unioned with its
/// direct assignments by the Policy Engine (via <see cref="GroupRoleResolver"/>).
/// </summary>
public sealed class GroupDirectory(
    IDataAccess<GroupRecord> groups,
    IDataAccess<GroupMembershipRecord> memberships,
    IDataAccess<GroupRoleRecord> groupRoles,
    AuditLog auditLog,
    Identity.PrincipalRoleCache? cache = null)
{
    public async Task<GroupRecord> CreateAsync(string name, string? description = null, CancellationToken ct = default)
    {
        var group = new GroupRecord
        {
            Id = GroupRecord.IdFor(Tenants.DefaultTenantId, name),
            TenantId = Tenants.DefaultTenantId,
            Name = name,
            Description = description
        };
        await groups.SaveAsync(group, ct);
        await auditLog.AppendAsync("group-directory", "group.created", group.Id.ToString(), name, ct: ct);
        return group;
    }

    public async Task<IReadOnlyList<GroupRecord>> ListAsync(CancellationToken ct = default)
        => (await groups.ReadAsync(ct)).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<GroupRecord?> GetAsync(Guid groupId, CancellationToken ct = default)
        => await groups.ReadAsync(groupId, ct);

    public async Task AddMemberAsync(Guid groupId, Guid principalId, CancellationToken ct = default)
    {
        await memberships.SaveAsync(new GroupMembershipRecord
        {
            Id = GroupMembershipRecord.IdFor(groupId, principalId),
            GroupId = groupId,
            PrincipalId = principalId
        }, ct);
        cache?.Invalidate(principalId);
        await auditLog.AppendAsync("group-directory", "group.member-added",
            groupId.ToString(), principalId.ToString(), ct: ct);
    }

    public async Task<bool> RemoveMemberAsync(Guid groupId, Guid principalId, CancellationToken ct = default)
    {
        var removed = await memberships.RemoveAsync(GroupMembershipRecord.IdFor(groupId, principalId), ct);
        cache?.Invalidate(principalId);
        if (removed)
            await auditLog.AppendAsync("group-directory", "group.member-removed",
                groupId.ToString(), principalId.ToString(), ct: ct);
        return removed;
    }

    public async Task AssignRoleAsync(Guid groupId, string roleName, CancellationToken ct = default)
    {
        if (!BuiltInRoles.Exists(roleName))
            throw new ArgumentException($"Unknown role '{roleName}'.", nameof(roleName));
        await groupRoles.SaveAsync(new GroupRoleRecord
        {
            Id = GroupRoleRecord.IdFor(groupId, roleName),
            GroupId = groupId,
            RoleName = roleName
        }, ct);
        // A group-role change fans out to every member — clear rather than track membership here.
        cache?.Clear();
        await auditLog.AppendAsync("group-directory", "group.role-assigned", groupId.ToString(), roleName, ct: ct);
    }

    public async Task<bool> RevokeRoleAsync(Guid groupId, string roleName, CancellationToken ct = default)
    {
        var removed = await groupRoles.RemoveAsync(GroupRoleRecord.IdFor(groupId, roleName), ct);
        cache?.Clear();
        if (removed)
            await auditLog.AppendAsync("group-directory", "group.role-revoked", groupId.ToString(), roleName, ct: ct);
        return removed;
    }

    public async Task<IReadOnlyList<Guid>> MembersAsync(Guid groupId, CancellationToken ct = default)
        => (await memberships.ReadAsync(ct)).Where(m => m.GroupId == groupId).Select(m => m.PrincipalId).ToList();

    public async Task<IReadOnlyList<string>> RolesAsync(Guid groupId, CancellationToken ct = default)
        => (await groupRoles.ReadAsync(ct)).Where(r => r.GroupId == groupId).Select(r => r.RoleName).ToList();
}
