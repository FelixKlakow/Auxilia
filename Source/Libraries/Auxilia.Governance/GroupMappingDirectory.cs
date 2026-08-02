using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance;

/// <summary>
/// Administration of identity-provider group→role mappings — the administrator-maintained table
/// <see cref="Identity.GroupMappingResolver"/> consults at federated sign-in. Every mutation is
/// audited; the target role must be a known built-in role.
/// </summary>
public sealed class GroupMappingDirectory(IDataAccess<GroupMappingRecord> mappings, AuditLog auditLog)
{
    public async Task<GroupMappingRecord> CreateAsync(
        string identityProvider, string groupClaim, string roleName, CancellationToken ct = default)
    {
        if (!BuiltInRoles.Exists(roleName))
            throw new ArgumentException($"Unknown role '{roleName}'.", nameof(roleName));

        var record = new GroupMappingRecord
        {
            Id = GroupMappingRecord.IdFor(identityProvider, groupClaim, roleName),
            IdentityProvider = identityProvider,
            GroupClaim = groupClaim,
            RoleName = roleName
        };
        await mappings.SaveAsync(record, ct);
        await auditLog.AppendAsync("group-mapping-directory", "group-mapping.created",
            record.Id.ToString(), $"{identityProvider}:{groupClaim}->{roleName}", ct: ct);
        return record;
    }

    public async Task<IReadOnlyList<GroupMappingRecord>> ListAsync(CancellationToken ct = default)
        => (await mappings.ReadAsync(ct))
            .OrderBy(m => m.IdentityProvider)
            .ThenBy(m => m.GroupClaim)
            .ToList();

    public async Task<bool> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var removed = await mappings.RemoveAsync(id, ct);
        if (removed)
            await auditLog.AppendAsync("group-mapping-directory", "group-mapping.removed", id.ToString(), "", ct: ct);
        return removed;
    }
}
