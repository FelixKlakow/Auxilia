namespace Auxilia.Governance.Identity;

/// <summary>
/// Authenticates credentials into an <see cref="IdentitySession"/>. The Local provider ships
/// in v1; OIDC/AAD/LDAP providers are later drop-ins behind the same interface.
/// </summary>
public interface IIdentityProvider
{
    /// <summary>Returns null when the credentials do not authenticate an active principal.</summary>
    Task<IdentitySession?> AuthenticatePasswordAsync(string username, string password, CancellationToken ct = default);

    /// <summary>Returns null when the API key does not authenticate an active principal.</summary>
    Task<IdentitySession?> AuthenticateApiKeyAsync(string apiKey, CancellationToken ct = default);
}
