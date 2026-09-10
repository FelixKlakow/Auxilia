namespace Auxilia.Governance.Identity;

/// <summary>
/// The effective role set a session carries: direct assignments unioned with the roles held
/// through first-class group memberships (cached beside the principal entry, exactly as the
/// Policy Engine caches them). Shared by every <see cref="IdentitySession"/> producer so
/// <c>/auth/me</c>, claims-based permission checks, and visibility filters agree with policy.
/// </summary>
internal static class EffectiveRoles
{
    public static async Task<IReadOnlyList<string>> ResolveAsync(
        Guid principalId,
        IReadOnlyList<string> directRoles,
        GroupRoleResolver? groupRoleResolver,
        PrincipalRoleCache? cache,
        CancellationToken ct)
    {
        if (groupRoleResolver is null)
            return directRoles.ToList();

        if (cache is null || !cache.TryGetGroupRoles(principalId, out var groupRoles))
        {
            groupRoles = (await groupRoleResolver.RolesForAsync(principalId, ct)).ToList();
            cache?.SetGroupRoles(principalId, groupRoles);
        }
        return directRoles.Concat(groupRoles).Distinct(StringComparer.Ordinal).ToList();
    }
}
