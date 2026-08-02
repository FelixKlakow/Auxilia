using Auxilia.Core.Contracts;
using Auxilia.Governance;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Read side of principal administration: lists/filters principals and composes each principal's full
/// role picture — direct assignments (with their stored source) unioned with roles held through
/// group membership. Mutations go through <see cref="PrincipalDirectory"/>, which audits them.
/// </summary>
public sealed class PrincipalAdminService(
    IDataAccess<PrincipalRecord> principals,
    IDataAccess<RoleAssignmentRecord> roleAssignments,
    GroupRoleResolver groupRoleResolver)
{
    public async Task<PagedResult<PrincipalDto>> QueryAsync(PrincipalQuery query, CancellationToken ct = default)
    {
        var matched = PrincipalQueryFilter.Apply(await principals.ReadAsync(ct), query).ToList();
        var take = query.Take <= 0 ? 50 : query.Take;
        var page = matched.Skip(query.Skip).Take(take).ToList();

        var assignments = await roleAssignments.ReadAsync(ct);
        var dtos = new List<PrincipalDto>(page.Count);
        foreach (var principal in page)
            dtos.Add(await ToDtoAsync(principal, assignments, ct));
        return new PagedResult<PrincipalDto>(dtos, matched.Count, query.Skip, take);
    }

    public async Task<PrincipalDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var principal = await principals.ReadAsync(id, ct);
        return principal is null ? null : await ToDtoAsync(principal, await roleAssignments.ReadAsync(ct), ct);
    }

    private async Task<PrincipalDto> ToDtoAsync(
        PrincipalRecord principal, IEnumerable<RoleAssignmentRecord> assignments, CancellationToken ct)
    {
        var roles = assignments
            .Where(a => a.PrincipalId == principal.Id)
            .Select(a => new PrincipalRoleDto(a.RoleName, a.Source))
            .ToList();
        // Union in roles held through first-class group membership (never stored as assignments).
        foreach (var role in await groupRoleResolver.RolesForAsync(principal.Id, ct))
            if (roles.All(r => r.RoleName != role))
                roles.Add(new PrincipalRoleDto(role, "Group"));
        return ToDto(principal, roles.OrderBy(r => r.RoleName, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Maps a principal record to its DTO with the given roles (a fresh principal has none).</summary>
    public static PrincipalDto ToDto(PrincipalRecord principal, IReadOnlyList<PrincipalRoleDto> roles)
        => new(principal.Id, principal.Kind, principal.DisplayName, principal.Status,
            principal.ExternalSubject, roles);
}

/// <summary>Pure, unit-testable filter + ordering for the principal query (kind, enabled, name/subject search).</summary>
public static class PrincipalQueryFilter
{
    public static IEnumerable<PrincipalRecord> Apply(IEnumerable<PrincipalRecord> source, PrincipalQuery query)
    {
        var result = source;
        if (!string.IsNullOrWhiteSpace(query.Kind))
            result = result.Where(p => string.Equals(p.Kind, query.Kind, StringComparison.OrdinalIgnoreCase));
        if (query.Enabled is { } enabled)
            result = result.Where(p => (p.Status == "Active") == enabled);
        if (!string.IsNullOrWhiteSpace(query.Search))
            result = result.Where(p =>
                p.DisplayName.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
                (p.ExternalSubject?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false));
        return result.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase);
    }
}
