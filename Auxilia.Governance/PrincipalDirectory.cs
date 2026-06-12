using System.Security.Cryptography;
using Auxilia.Governance.Identity;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance;

/// <summary>
/// Administration of principals, their role assignments, and local credentials.
/// Every mutating operation is audited.
/// </summary>
public sealed class PrincipalDirectory(
    IDataAccess<PrincipalRecord> principals,
    IDataAccess<RoleAssignmentRecord> roleAssignments,
    IDataAccess<CredentialRecord> credentials,
    AuditLog auditLog)
{
    public async Task<PrincipalRecord> CreateHumanAsync(
        string displayName, string username, string password, CancellationToken ct = default)
    {
        var principal = new PrincipalRecord
        {
            TenantId = Tenants.DefaultTenantId,
            Kind = "Human",
            DisplayName = displayName,
            Status = "Active"
        };
        await principals.SaveAsync(principal, ct);
        await credentials.SaveAsync(new CredentialRecord
        {
            Id = CredentialRecord.IdForPassword(username),
            PrincipalId = principal.Id,
            Kind = "Password",
            Identifier = username,
            SecretHash = PasswordHasher.Hash(password)
        }, ct);
        await auditLog.AppendAsync("principal-directory", "principal.created",
            principal.Id.ToString(), "human", ct: ct);
        return principal;
    }

    /// <summary>Creates an AI or service principal and returns it with its generated API key.</summary>
    public async Task<(PrincipalRecord Principal, string ApiKey)> CreateApiKeyPrincipalAsync(
        string displayName, string kind, CancellationToken ct = default)
    {
        if (kind is not ("AiAgent" or "Service"))
            throw new ArgumentException("API-key principals must be of kind AiAgent or Service.", nameof(kind));

        var principal = new PrincipalRecord
        {
            TenantId = Tenants.DefaultTenantId,
            Kind = kind,
            DisplayName = displayName,
            Status = "Active"
        };
        await principals.SaveAsync(principal, ct);

        var apiKey = "aux_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await credentials.SaveAsync(new CredentialRecord
        {
            Id = CredentialRecord.IdForApiKeyHash(LocalIdentityProvider.HashApiKey(apiKey)),
            PrincipalId = principal.Id,
            Kind = "ApiKey",
            Identifier = principal.Id.ToString("D"),
            SecretHash = LocalIdentityProvider.HashApiKey(apiKey)
        }, ct);
        await auditLog.AppendAsync("principal-directory", "principal.created",
            principal.Id.ToString(), kind, ct: ct);
        return (principal, apiKey);
    }

    public async Task AssignRoleAsync(Guid principalId, string roleName, CancellationToken ct = default)
    {
        if (!BuiltInRoles.Exists(roleName))
            throw new ArgumentException($"Unknown role '{roleName}'.", nameof(roleName));

        await roleAssignments.SaveAsync(new RoleAssignmentRecord
        {
            Id = RoleAssignmentRecord.IdFor(principalId, roleName),
            PrincipalId = principalId,
            RoleName = roleName,
            Source = "Direct"
        }, ct);
        await auditLog.AppendAsync("principal-directory", "role.assigned",
            principalId.ToString(), roleName, ct: ct);
    }

    public async Task<bool> RevokeRoleAsync(Guid principalId, string roleName, CancellationToken ct = default)
    {
        var removed = await roleAssignments.RemoveAsync(RoleAssignmentRecord.IdFor(principalId, roleName), ct);
        if (removed)
            await auditLog.AppendAsync("principal-directory", "role.revoked",
                principalId.ToString(), roleName, ct: ct);
        return removed;
    }

    public async Task<bool> DisableAsync(Guid principalId, CancellationToken ct = default)
    {
        var principal = await principals.ReadAsync(principalId, ct);
        if (principal is null)
            return false;

        await principals.SaveAsync(principal with { Status = "Disabled" }, ct);
        await auditLog.AppendAsync("principal-directory", "principal.disabled",
            principalId.ToString(), "disabled", ct: ct);
        return true;
    }

    public async Task<bool> AnyAdministratorExistsAsync(CancellationToken ct = default)
    {
        var assignmentsQuery = await roleAssignments.ReadAsync(ct);
        return assignmentsQuery.Any(a => a.RoleName == BuiltInRoles.Administrator);
    }
}
