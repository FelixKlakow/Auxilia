namespace Auxilia.Core.Api.Auth;

/// <summary>Security configuration for the Core API (bound from the <c>CoreSecurity</c> section).</summary>
public sealed class CoreSecuritySettings
{
    /// <summary>
    /// When set, an Administrator API-key principal with this exact key is ensured at startup
    /// (idempotent). Used to bootstrap automation and system tests; production issues keys via
    /// the principal directory instead.
    /// </summary>
    public string? BootstrapApiKey { get; set; }

    public string BootstrapPrincipalName { get; set; } = "core-bootstrap";

    /// <summary>
    /// Lifetime (in minutes) of a per-user bearer token minted by <c>POST /auth/token</c> for a
    /// delegated console caller. Kept short — the console re-mints from its live cookie session.
    /// </summary>
    public int UserTokenLifetimeMinutes { get; set; } = 30;

    /// <summary>
    /// Lifetime (in minutes) of a per-user bearer minted by <c>POST /auth/login</c> for desktop/CLI
    /// clients. Longer than the console token — a desktop client has no cookie session to silently
    /// re-mint from, and must not hold the user's password to renew. Default: one week. This is the
    /// FALLBACK; an administrator changes it at runtime via the platform-settings surface.
    /// </summary>
    public int LoginTokenLifetimeMinutes { get; set; } = 10080;
}
