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
}
