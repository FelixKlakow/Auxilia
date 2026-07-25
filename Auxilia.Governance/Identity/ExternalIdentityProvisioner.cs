using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance.Identity;

/// <summary>
/// Just-in-time provisioning for federated (OIDC/Entra) sign-ins: turns a validated
/// <see cref="ExternalIdentity"/> into an <see cref="IdentitySession"/>, creating the backing
/// <see cref="PrincipalRecord"/> on first sight — keyed deterministically by external subject, with
/// no local credential ever stored. Directory-group → role mapping layers on top of this later; the
/// session here carries the principal's direct role assignments (the Policy Engine unions the rest).
/// </summary>
public sealed class ExternalIdentityProvisioner(
    IDataAccess<PrincipalRecord> principals,
    IDataAccess<RoleAssignmentRecord> roleAssignments,
    AuditLog auditLog)
{
    /// <summary>
    /// Resolves (creating on first sight) the principal for an external identity and returns its
    /// session. Returns <c>null</c> when the account exists but is disabled — signing in again must
    /// never silently re-enable it.
    /// </summary>
    public async Task<IdentitySession?> ProvisionAsync(ExternalIdentity identity, CancellationToken ct = default)
    {
        var subjectKey = SubjectKey(identity.Provider, identity.Subject);
        var principalId = DeterministicGuid.For("principal-external", subjectKey);

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
                Status = "Active"
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
        else if (principal.DisplayName != identity.DisplayName)
        {
            // Keep the display name current with the directory (the source of truth).
            principal = principal with { DisplayName = identity.DisplayName };
            await principals.SaveAsync(principal, ct);
        }

        await auditLog.AppendAsync("external-identity", "principal.signed-in",
            principal.Id.ToString(), identity.Provider, ct: ct);

        var assignments = await roleAssignments.ReadAsync(ct);
        var roles = assignments
            .Where(a => a.PrincipalId == principal.Id)
            .Select(a => a.RoleName)
            .ToList();

        return new IdentitySession(principal.Id, principal.Kind, principal.DisplayName, roles);
    }

    private static string SubjectKey(string provider, string subject) => $"{provider}|{subject}";
}
