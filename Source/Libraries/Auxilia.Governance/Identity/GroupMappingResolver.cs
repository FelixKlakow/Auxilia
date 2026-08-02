using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance.Identity;

/// <summary>
/// Translates an external identity provider's group claims into Auxilia role names via the
/// administrator-maintained mappings. Evaluated at session start.
/// </summary>
public sealed class GroupMappingResolver(IDataAccess<GroupMappingRecord> mappings)
{
    public async Task<IReadOnlyList<string>> ResolveRolesAsync(
        string identityProvider, IReadOnlyCollection<string> groupClaims, CancellationToken ct = default)
    {
        var query = await mappings.ReadAsync(ct);
        return query
            .Where(m => m.IdentityProvider == identityProvider)
            .ToList()
            .Where(m => groupClaims.Contains(m.GroupClaim))
            .Select(m => m.RoleName)
            .Distinct()
            .ToList();
    }
}
