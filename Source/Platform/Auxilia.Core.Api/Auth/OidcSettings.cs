namespace Auxilia.Core.Api.Auth;

/// <summary>
/// Entra ID (OIDC) relying-party configuration, bound from the <c>Oidc</c> section. When
/// <see cref="Enabled"/> is false the interactive OIDC scheme is not registered and only local
/// password + API-key authentication are available.
/// </summary>
public sealed class OidcSettings
{
    public bool Enabled { get; set; }

    /// <summary>OIDC authority, e.g. <c>https://login.microsoftonline.com/{tenantId}/v2.0</c>.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Provider name recorded on provisioned principals' external subject.</summary>
    public string ProviderName { get; set; } = "entra";

    /// <summary>Redirect path Entra returns the authorization code to.</summary>
    public string CallbackPath { get; set; } = "/auth/oidc-callback";

    /// <summary>Extra scopes beyond openid/profile/email (e.g. a group scope) requested at sign-in.</summary>
    public IList<string> Scopes { get; set; } = new List<string>();

    /// <summary>
    /// When true, the user's access token is retained (encrypted, session-lifetime) at sign-in so
    /// workflows can act on-behalf-of the user via OBO delegation. Off by default — retaining a user
    /// token is opt-in. No refresh token is ever stored.
    /// </summary>
    public bool EnableDelegation { get; set; }
}
