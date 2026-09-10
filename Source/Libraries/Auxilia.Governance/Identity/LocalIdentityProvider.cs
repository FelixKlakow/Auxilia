using System.Security.Cryptography;
using System.Text;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance.Identity;

/// <summary>
/// Local authentication (PBKDF2 passwords, SHA-256 API keys). The session's <see cref="IdentitySession.Roles"/>
/// are the principal's EFFECTIVE roles — direct assignments unioned with first-class-group roles
/// (<see cref="GroupRoleResolver"/>) — so claims-based checks see what the Policy Engine sees.
/// </summary>
public sealed class LocalIdentityProvider(
    IDataAccess<CredentialRecord> credentials,
    IDataAccess<PrincipalRecord> principals,
    IDataAccess<RoleAssignmentRecord> roleAssignments,
    PrincipalRoleCache? cache = null,
    GroupRoleResolver? groupRoles = null) : IIdentityProvider
{
    public async Task<IdentitySession?> AuthenticatePasswordAsync(
        string username, string password, CancellationToken ct = default)
    {
        var credential = await credentials.ReadAsync(CredentialRecord.IdForPassword(username), ct);
        if (credential is null || !PasswordHasher.Verify(password, credential.SecretHash))
            return null;

        return await SessionForAsync(credential.PrincipalId, ct);
    }

    public async Task<IdentitySession?> AuthenticateApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        var keyHash = HashApiKey(apiKey);
        var credential = await credentials.ReadAsync(CredentialRecord.IdForApiKeyHash(keyHash), ct);
        if (credential is null)
            return null;

        return await SessionForAsync(credential.PrincipalId, ct);
    }

    public Task<IdentitySession?> ResolveSessionAsync(Guid principalId, CancellationToken ct = default)
        => SessionForAsync(principalId, ct);

    private async Task<IdentitySession?> SessionForAsync(Guid principalId, CancellationToken ct)
    {
        PrincipalRecord? principal;
        IReadOnlyList<string> roles;
        if (cache is null || !cache.TryGetPrincipal(principalId, out principal, out roles))
        {
            principal = await principals.ReadAsync(principalId, ct);
            var assignmentsQuery = await roleAssignments.ReadAsync(ct);
            roles = assignmentsQuery
                .Where(a => a.PrincipalId == principalId)
                .Select(a => a.RoleName)
                .ToList();
            cache?.SetPrincipal(principalId, principal, roles);
        }

        if (principal is null || principal.Status != "Active")
            return null;

        var effectiveRoles = await EffectiveRoles.ResolveAsync(principalId, roles, groupRoles, cache, ct);
        return new IdentitySession(principal.Id, principal.Kind, principal.DisplayName, effectiveRoles);
    }

    public static string HashApiKey(string apiKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}
