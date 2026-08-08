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
    AuditLog auditLog,
    PrincipalRoleCache? cache = null,
    GroupRoleResolver? groupRoles = null)
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

    /// <summary>Creates a service principal (tags classify it further) with its generated API key.</summary>
    public async Task<(PrincipalRecord Principal, string ApiKey)> CreateApiKeyPrincipalAsync(
        string displayName, CancellationToken ct = default)
    {
        var principal = new PrincipalRecord
        {
            TenantId = Tenants.DefaultTenantId,
            Kind = "Service",
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
            principal.Id.ToString(), "service", ct: ct);
        return (principal, apiKey);
    }

    /// <summary>Replaces a principal's tags (trimmed, de-duplicated; empty clears them).</summary>
    public async Task<bool> SetTagsAsync(
        Guid principalId, IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        var principal = await principals.ReadAsync(principalId, ct);
        if (principal is null)
            return false;

        var cleaned = tags
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        await principals.SaveAsync(principal with
        {
            TagsJson = System.Text.Json.JsonSerializer.Serialize(cleaned)
        }, ct);
        await auditLog.AppendAsync("principal-directory", "principal.tags-set",
            principalId.ToString(), string.Join(",", cleaned), ct: ct);
        return true;
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
        cache?.Invalidate(principalId);
        await auditLog.AppendAsync("principal-directory", "role.assigned",
            principalId.ToString(), roleName, ct: ct);
    }

    public async Task<bool> RevokeRoleAsync(Guid principalId, string roleName, CancellationToken ct = default)
    {
        // Lock-out guard: the platform must never end up without an enabled administrator. The
        // revoke is refused only when it would remove the target's LAST admin source and no other
        // enabled administrator remains.
        if (roleName == BuiltInRoles.Administrator
            && !await HoldsAdminViaGroupAsync(principalId, ct)
            && await IsLastEnabledAdministratorAsync(principalId, ct))
            throw new InvalidOperationException(
                "This is the last enabled administrator — assign the Administrator role to someone else first.");

        var removed = await roleAssignments.RemoveAsync(RoleAssignmentRecord.IdFor(principalId, roleName), ct);
        cache?.Invalidate(principalId);
        if (removed)
            await auditLog.AppendAsync("principal-directory", "role.revoked",
                principalId.ToString(), roleName, ct: ct);
        return removed;
    }

    public Task<bool> DisableAsync(Guid principalId, CancellationToken ct = default)
        => SetEnabledAsync(principalId, false, ct);

    /// <summary>Enables or disables a principal; a disabled principal can no longer authenticate.</summary>
    public async Task<bool> SetEnabledAsync(Guid principalId, bool enabled, CancellationToken ct = default)
    {
        var principal = await principals.ReadAsync(principalId, ct);
        if (principal is null)
            return false;

        // Lock-out guard: disabling the last enabled administrator would leave no one able to
        // administer principals (including re-enabling anyone).
        if (!enabled
            && await IsAdministratorAsync(principalId, ct)
            && await IsLastEnabledAdministratorAsync(principalId, ct))
            throw new InvalidOperationException(
                "This is the last enabled administrator — it cannot be disabled.");

        var status = enabled ? "Active" : "Disabled";
        await principals.SaveAsync(principal with { Status = status }, ct);
        // A disabled principal must stop authenticating NOW, not at TTL expiry.
        cache?.Invalidate(principalId);
        await auditLog.AppendAsync("principal-directory", enabled ? "principal.enabled" : "principal.disabled",
            principalId.ToString(), status.ToLowerInvariant(), ct: ct);
        return true;
    }

    /// <summary>
    /// Re-proves the principal's OWN credential for step-up flows: the password of a human, the
    /// API key of a service principal. Never authenticates — the caller is already signed in;
    /// this only confirms the person at the keyboard still holds the credential.
    /// </summary>
    public async Task<bool> VerifySecretAsync(Guid principalId, string secret, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(secret))
            return false;
        var all = await credentials.ReadAsync(ct);
        foreach (var credential in all.Where(c => c.PrincipalId == principalId))
        {
            var verified = credential.Kind switch
            {
                "Password" => PasswordHasher.Verify(secret, credential.SecretHash),
                "ApiKey" => string.Equals(
                    LocalIdentityProvider.HashApiKey(secret), credential.SecretHash, StringComparison.Ordinal),
                _ => false,
            };
            if (verified)
                return true;
        }
        return false;
    }

    public async Task<bool> AnyAdministratorExistsAsync(CancellationToken ct = default)
    {
        var assignmentsQuery = await roleAssignments.ReadAsync(ct);
        return assignmentsQuery.Any(a => a.RoleName == BuiltInRoles.Administrator);
    }

    /// <summary>True when the principal holds Administrator, directly or via a group role.</summary>
    public async Task<bool> IsAdministratorAsync(Guid principalId, CancellationToken ct)
    {
        var assignments = await roleAssignments.ReadAsync(ct);
        return assignments.Any(a => a.PrincipalId == principalId && a.RoleName == BuiltInRoles.Administrator)
               || await HoldsAdminViaGroupAsync(principalId, ct);
    }

    private async Task<bool> HoldsAdminViaGroupAsync(Guid principalId, CancellationToken ct)
        => groupRoles is not null
           && (await groupRoles.RolesForAsync(principalId, ct)).Contains(BuiltInRoles.Administrator);

    /// <summary>True when no OTHER enabled principal holds Administrator (direct or via groups).</summary>
    private async Task<bool> IsLastEnabledAdministratorAsync(Guid principalId, CancellationToken ct)
    {
        var assignments = await roleAssignments.ReadAsync(ct);
        var directAdmins = assignments
            .Where(a => a.RoleName == BuiltInRoles.Administrator)
            .Select(a => a.PrincipalId)
            .ToHashSet();
        var all = await principals.ReadAsync(ct);
        foreach (var other in all.Where(p => p.Id != principalId && p.Status == "Active"))
        {
            if (directAdmins.Contains(other.Id) || await HoldsAdminViaGroupAsync(other.Id, ct))
                return false;
        }
        return true;
    }
}
