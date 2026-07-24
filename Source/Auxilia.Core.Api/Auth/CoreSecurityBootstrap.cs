using Auxilia.Governance;
using Auxilia.Governance.Identity;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Auth;

/// <summary>
/// Ensures a bootstrap Administrator API-key principal from configuration, idempotent by the
/// key's hash. Writes directly into the governance stores (no plaintext key is ever persisted).
/// </summary>
public sealed class CoreSecurityBootstrap(
    IDataAccess<PrincipalRecord> principals,
    IDataAccess<CredentialRecord> credentials,
    IDataAccess<RoleAssignmentRecord> roleAssignments)
{
    public async Task EnsureAsync(CoreSecuritySettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.BootstrapApiKey))
            return;

        var hash = LocalIdentityProvider.HashApiKey(settings.BootstrapApiKey);
        var credentialId = CredentialRecord.IdForApiKeyHash(hash);
        if (await credentials.ReadAsync(credentialId, ct) is not null)
            return;

        var principal = new PrincipalRecord
        {
            TenantId = Tenants.DefaultTenantId,
            Kind = "Service",
            DisplayName = settings.BootstrapPrincipalName,
            Status = "Active"
        };
        await principals.SaveAsync(principal, ct);
        await credentials.SaveAsync(new CredentialRecord
        {
            Id = credentialId,
            PrincipalId = principal.Id,
            Kind = "ApiKey",
            Identifier = principal.Id.ToString("D"),
            SecretHash = hash
        }, ct);
        await roleAssignments.SaveAsync(new RoleAssignmentRecord
        {
            Id = RoleAssignmentRecord.IdFor(principal.Id, BuiltInRoles.Administrator),
            PrincipalId = principal.Id,
            RoleName = BuiltInRoles.Administrator,
            Source = "Direct"
        }, ct);
    }
}
