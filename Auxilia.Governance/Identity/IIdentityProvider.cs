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

    /// <summary>
    /// Resolves the session for an already-identified principal (e.g. one bound to a signed,
    /// short-lived bearer token minted by the Core). Returns null when the principal no longer
    /// exists or is not Active — a disabled principal's outstanding token must never authenticate.
    /// </summary>
    Task<IdentitySession?> ResolveSessionAsync(Guid principalId, CancellationToken ct = default);
}
