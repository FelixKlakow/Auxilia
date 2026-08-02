using System.Text.Encodings.Web;
using Auxilia.Governance.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Auth;

/// <summary>
/// Authenticates a delegated console caller by a short-lived, Core-minted per-user bearer token
/// (<c>Authorization: Bearer auxu_…</c>). The token is validated (signature + expiry) and its principal
/// re-resolved to the same <c>auxilia:principal-id</c> claim the cookie/API-key paths produce, so the
/// Policy Engine authorizes it as the real user. A token whose principal is no longer Active fails.
/// A request whose bearer is not a user token defers (NoResult) to the API-key scheme.
/// </summary>
public sealed class CoreUserBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    UserBearerTokenService tokens,
    IIdentityProvider identityProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CoreUserBearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        // Not a user bearer (or expired/tampered) → defer so the API-key scheme can try the same header.
        if (tokens.Validate(token) is not { } principalId)
            return AuthenticateResult.NoResult();

        // The token is cryptographically valid; the principal must still be Active — a disabled
        // principal's outstanding token must not authenticate (parity with LocalIdentityProvider).
        var session = await identityProvider.ResolveSessionAsync(principalId, Context.RequestAborted);
        if (session is null)
            return AuthenticateResult.Fail("The token's principal is no longer active.");

        return AuthenticateResult.Success(
            new AuthenticationTicket(CoreClaims.ToClaimsPrincipal(session, SchemeName), SchemeName));
    }
}
