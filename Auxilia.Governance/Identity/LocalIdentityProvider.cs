using System.Security.Cryptography;
using System.Text;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance.Identity;

public sealed class LocalIdentityProvider(
    IDataAccess<CredentialRecord> credentials,
    IDataAccess<PrincipalRecord> principals,
    IDataAccess<RoleAssignmentRecord> roleAssignments) : IIdentityProvider
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

    private async Task<IdentitySession?> SessionForAsync(Guid principalId, CancellationToken ct)
    {
        var principal = await principals.ReadAsync(principalId, ct);
        if (principal is null || principal.Status != "Active")
            return null;

        var assignmentsQuery = await roleAssignments.ReadAsync(ct);
        var roles = assignmentsQuery
            .Where(a => a.PrincipalId == principalId)
            .Select(a => a.RoleName)
            .ToList();

        return new IdentitySession(principal.Id, principal.Kind, principal.DisplayName, roles);
    }

    public static string HashApiKey(string apiKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}
