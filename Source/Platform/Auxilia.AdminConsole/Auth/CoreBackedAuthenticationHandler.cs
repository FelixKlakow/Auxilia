using System.Security.Claims;
using System.Text.Encodings.Web;
using Auxilia.Core.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Auxilia.AdminConsole.Auth;

/// <summary>
/// Endpoint-level twin of <see cref="ConsoleAuthenticationStateProvider"/>: authenticates the
/// incoming HTTP request by asking the Core who the caller is (per-user bearer relayed from the
/// session cookie, or the configured app key). Without a registered scheme, every
/// <c>[Authorize]</c> page 500s on its initial request — the console has no local identity store,
/// so the Core stays the only authority here too. An unauthenticated request is challenged to the
/// Core's sign-in (same-origin gateway deployment contract).
/// </summary>
public sealed class CoreBackedAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    CoreClientOptions coreOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "AuxiliaCoreBacked";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var core = Context.RequestServices.GetRequiredService<ICoreClient>();
        try
        {
            var principal = await core.GetCurrentPrincipalAsync(Context.RequestAborted);
            var identity = ConsoleAuthenticationStateProvider.IdentityOf(principal);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
        }
        catch (CoreApiException)
        {
            return AuthenticateResult.NoResult();
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Redirect($"{coreOptions.BaseAddress.TrimEnd('/')}/auth/login");
        return Task.CompletedTask;
    }
}
