using System.Text.Json;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance.Identity;

/// <summary>
/// Just-in-time provisioning for federated (OIDC/Entra) sign-ins: turns a validated
/// <see cref="ExternalIdentity"/> into an <see cref="IdentitySession"/>, creating the backing
/// <see cref="PrincipalRecord"/> on first sight — keyed deterministically by external subject, with
/// no local credential ever stored. Directory group claims are mapped to roles and reconciled onto
/// the principal at every sign-in (the directory is the source of truth); the Policy Engine then
/// unions these with the principal's direct and first-class-group roles.
/// </summary>
public sealed class ExternalIdentityProvisioner(
    IDataAccess<PrincipalRecord> principals,
    IDataAccess<RoleAssignmentRecord> roleAssignments,
    GroupMappingResolver groupMappingResolver,
    AuditLog auditLog,
    PrincipalRoleCache? cache = null)
{
    /// <summary>Source tag for role assignments derived from IdP group claims (see <see cref="RoleAssignmentRecord.Source"/>).</summary>
    private const string DirectorySource = "GroupMapping";

    /// <summary>
    /// Resolves (creating on first sight) the principal for an external identity and returns its
    /// session. Returns <c>null</c> when the account exists but is disabled — signing in again must
    /// never silently re-enable it.
    /// </summary>
    public async Task<IdentitySession?> ProvisionAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        var subjectKey = SubjectKey(identity.Provider, identity.Subject);
        var principalId = DeterministicGuid.For("principal-external", subjectKey);
        var groupsJson = JsonSerializer.Serialize(identity.Groups);

        var principal = await principals.ReadAsync(principalId, ct);
        if (principal is null)
        {
            principal = new PrincipalRecord
            {
                Id = principalId,
                TenantId = Tenants.DefaultTenantId,
                Kind = "Human",
                DisplayName = identity.DisplayName,
                ExternalSubject = subjectKey,
                Status = "Active",
                DirectoryGroupsJson = groupsJson
            };
            await principals.SaveAsync(principal, ct);
            await auditLog.AppendAsync("external-identity", "principal.provisioned",
                principal.Id.ToString(), identity.Provider, ct: ct);
        }
        else if (principal.Status != "Active")
        {
            await auditLog.AppendAsync("external-identity", "principal.sign-in-denied",
                principal.Id.ToString(), "disabled", ct: ct);
            return null;
        }
        else if (principal.DisplayName != identity.DisplayName || principal.DirectoryGroupsJson != groupsJson)
        {
            // Keep the display name and directory group memberships current with the directory
            // (the source of truth) — the latter gates AD-group-scoped connector access.
            principal = principal with { DisplayName = identity.DisplayName, DirectoryGroupsJson = groupsJson };
            await principals.SaveAsync(principal, ct);
        }

        await auditLog.AppendAsync("external-identity", "principal.signed-in",
            principal.Id.ToString(), identity.Provider, ct: ct);

        await ReconcileDirectoryRolesAsync(principal.Id, identity, ct);
        cache?.Invalidate(principal.Id);

        var assignments = await roleAssignments.ReadAsync(ct);
        var roles = assignments
            .Where(a => a.PrincipalId == principal.Id)
            .Select(a => a.RoleName)
            .ToList();

        return new IdentitySession(principal.Id, principal.Kind, principal.DisplayName, roles);
    }

    /// <summary>
    /// Reconciles the principal's directory-derived (<see cref="DirectorySource"/>) role assignments to
    /// exactly the set the current token's group claims map to: grants newly-mapped roles and revokes
    /// ones the directory no longer grants. Administered (<c>Direct</c>) and imported assignments are
    /// never touched, so a directory user who leaves a group loses only the group-granted role.
    /// </summary>
    private async Task ReconcileDirectoryRolesAsync(Guid principalId, ExternalIdentity identity, CancellationToken ct)
    {
        var wanted = (await groupMappingResolver.ResolveRolesAsync(identity.Provider, identity.Groups, ct))
            .Where(BuiltInRoles.Exists)
            .ToHashSet(StringComparer.Ordinal);

        var existing = (await roleAssignments.ReadAsync(ct))
            .Where(a => a.PrincipalId == principalId)
            .ToList();

        // Revoke directory-derived roles the token no longer grants (leave Direct/imported grants alone).
        foreach (var stale in existing.Where(a => a.Source == DirectorySource && !wanted.Contains(a.RoleName)))
        {
            await roleAssignments.RemoveAsync(stale.Id, ct);
            await auditLog.AppendAsync("external-identity", "role.revoked", principalId.ToString(), stale.RoleName, ct: ct);
        }

        // Grant newly-mapped roles not already held through any source.
        var alreadyHeld = existing.Select(a => a.RoleName).ToHashSet(StringComparer.Ordinal);
        foreach (var role in wanted.Where(r => !alreadyHeld.Contains(r)))
        {
            await roleAssignments.SaveAsync(new RoleAssignmentRecord
            {
                Id = RoleAssignmentRecord.IdFor(principalId, role),
                PrincipalId = principalId,
                RoleName = role,
                Source = DirectorySource
            }, ct);
            await auditLog.AppendAsync("external-identity", "role.assigned", principalId.ToString(), role, ct: ct);
        }
    }

    private static string SubjectKey(string provider, string subject) => $"{provider}|{subject}";
}
