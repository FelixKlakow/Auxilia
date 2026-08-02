using System.Text.Encodings.Web;
using Auxilia.Governance.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Auth;

/// <summary>
/// Bearer API-key authentication: clients send <c>Authorization: Bearer &lt;apiKey&gt;</c>,
/// resolved through the platform identity provider into a ClaimsPrincipal. The Core is the
/// single authentication authority for REST and MCP alike.
/// </summary>
public sealed class CoreApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IIdentityProvider identityProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CoreApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var apiKey = header["Bearer ".Length..].Trim();
        var session = await identityProvider.AuthenticateApiKeyAsync(apiKey, Context.RequestAborted);
        if (session is null)
            return AuthenticateResult.Fail("The API key does not authenticate an active principal.");

        return AuthenticateResult.Success(
            new AuthenticationTicket(CoreClaims.ToClaimsPrincipal(session), SchemeName));
    }
}
