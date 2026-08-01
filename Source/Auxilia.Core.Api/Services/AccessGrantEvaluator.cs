using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Evaluates the shared personal-resource sharing model (connectors, run configurations): a
/// grant list admits a specific principal, every member of a first-class platform group, or
/// every principal whose directory (AD) group membership — captured at the last federated
/// sign-in (<see cref="PrincipalRecord.DirectoryGroupsJson"/>) — includes a granted group.
/// </summary>
public sealed class AccessGrantEvaluator(
    IDataAccess<PrincipalRecord> principals,
    IDataAccess<GroupMembershipRecord> groupMemberships)
{
    public async Task<bool> IsGrantedAsync(string grantsJson, Guid principalId, CancellationToken ct)
    {
        var grants = JsonSerializer.Deserialize<List<AccessGrant>>(grantsJson) ?? [];
        if (grants.Count == 0)
            return false;

        if (grants.Any(g => g.Kind == AccessGrantKind.Principal
                            && Guid.TryParse(g.Id, out var granted) && granted == principalId))
            return true;

        var grantedGroupIds = grants
            .Where(g => g.Kind == AccessGrantKind.Group)
            .Select(g => Guid.TryParse(g.Id, out var groupId) ? groupId : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        if (grantedGroupIds.Count > 0
            && (await groupMemberships.ReadAsync(ct))
                .Any(m => m.PrincipalId == principalId && grantedGroupIds.Contains(m.GroupId)))
            return true;

        var directoryGrants = grants
            .Where(g => g.Kind == AccessGrantKind.DirectoryGroup)
            .Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (directoryGrants.Count == 0)
            return false;

        var principal = await principals.ReadAsync(principalId, ct);
        var principalGroups = JsonSerializer.Deserialize<List<string>>(principal?.DirectoryGroupsJson ?? "[]") ?? [];
        return principalGroups.Any(directoryGrants.Contains);
    }
}
