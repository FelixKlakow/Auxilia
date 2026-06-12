using System.Text.Encodings.Web;
using Auxilia.BackendService.Dashboard;
using Auxilia.Governance.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Auxilia.BackendService.Mcp;

/// <summary>
/// Bearer API-key authentication for the /mcp endpoint: AI and service principals send
/// <c>Authorization: Bearer &lt;apiKey&gt;</c>, which is resolved through the platform
/// identity provider into the same ClaimsPrincipal shape the dashboard cookie session uses.
/// </summary>
public sealed class McpApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IIdentityProvider identityProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "McpApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var apiKey = header["Bearer ".Length..].Trim();
        var session = await identityProvider.AuthenticateApiKeyAsync(apiKey, Context.RequestAborted);
        if (session is null)
            return AuthenticateResult.Fail("The API key does not authenticate an active principal.");

        var ticket = new AuthenticationTicket(
            DashboardAuthEndpoints.ToClaimsPrincipal(session), SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
