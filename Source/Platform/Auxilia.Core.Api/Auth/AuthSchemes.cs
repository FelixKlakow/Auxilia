namespace Auxilia.Core.Api.Auth;

/// <summary>Authentication scheme names used across the Core control plane.</summary>
public static class AuthSchemes
{
    /// <summary>Bearer API key — AI/service principals and MCP (the programmatic default).</summary>
    public const string ApiKey = CoreApiKeyAuthenticationHandler.SchemeName;

    /// <summary>Short-lived, Core-minted per-user bearer — a delegated console acting AS the signed-in user.</summary>
    public const string UserBearer = CoreUserBearerAuthenticationHandler.SchemeName;

    /// <summary>Browser session cookie issued after an interactive sign-in.</summary>
    public const string Cookie = "CoreCookie";

    /// <summary>The interactive external identity provider (Entra OIDC) challenge target.</summary>
    public const string Oidc = "CoreOidc";

    /// <summary>
    /// Temporary cookie holding the validated external identity between the OIDC callback and
    /// principal provisioning. Component tests register their stubbed external provider under this
    /// name (the scheme <c>/auth/callback</c> authenticates), which the API only registers itself
    /// when OIDC is enabled.
    /// </summary>
    public const string ExternalCookie = "CoreExternalCookie";
}
