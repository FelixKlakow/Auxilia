using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests;

/// <summary>
/// Stands in for the Entra OIDC external scheme in component tests: builds the "external identity"
/// from <c>X-Test-*</c> request headers so the real <c>/auth/callback</c> provisioning + session-
/// issuance path runs without a live tenant. Registered under <c>AuthSchemes.ExternalCookie</c>
/// (the scheme the callback authenticates), which the API only registers itself when OIDC is enabled.
/// </summary>
public sealed class StubExternalAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder), IAuthenticationSignOutHandler
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var subject = Request.Headers["X-Test-Sub"].ToString();
        if (string.IsNullOrEmpty(subject))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
        if (Request.Headers["X-Test-Name"].ToString() is { Length: > 0 } name)
            claims.Add(new Claim("name", name));
        if (Request.Headers["X-Test-Email"].ToString() is { Length: > 0 } email)
            claims.Add(new Claim(ClaimTypes.Email, email));
        foreach (var group in Request.Headers["X-Test-Groups"].ToString()
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            claims.Add(new Claim("groups", group));
        // Emulate Entra's group-claim overage signal (too many groups to embed in the token).
        if (Request.Headers["X-Test-Overage"].ToString() is "true")
            claims.Add(new Claim("hasgroups", "true"));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "StubExternal"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }

    // The callback signs the external identity out after provisioning; the stub is stateless.
    public Task SignOutAsync(AuthenticationProperties? properties) => Task.CompletedTask;
}
