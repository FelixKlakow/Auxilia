using System.Security.Claims;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Microsoft.AspNetCore.Components.Authorization;

namespace Auxilia.AdminConsole.Auth;

/// <summary>
/// Derives the console's authentication state from the Core: the signed-in principal and its roles come
/// from <see cref="ICoreClient.GetCurrentPrincipalAsync"/> (which the per-user bearer authorizes), so the
/// console holds no local username/password — Core is the identity authority. A failed/unauthenticated
/// call yields an anonymous principal, which routes the browser to Core's sign-in via
/// <c>RedirectToLogin</c>. Roles surface as <see cref="ClaimTypes.Role"/> claims and the Core-computed
/// effective permissions as <see cref="PermissionClaimType"/> claims for show/hide.
/// </summary>
public sealed class ConsoleAuthenticationStateProvider(ICoreClient core) : AuthenticationStateProvider
{
    public const string AuthenticationType = "AuxiliaCore";
    public const string PermissionClaimType = "auxilia:permission";

    /// <summary>Whether the signed-in user holds the given permission action (see <c>PermissionActions</c>).</summary>
    public static bool Can(ClaimsPrincipal user, string action)
        => user.HasClaim(PermissionClaimType, action);

    /// <summary>The console's claims identity for a Core-reported principal (shared with the
    /// endpoint-level <see cref="CoreBackedAuthenticationHandler"/>).</summary>
    public static ClaimsIdentity IdentityOf(CurrentPrincipal principal)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, principal.PrincipalId.ToString()),
            new(ClaimTypes.Name, principal.DisplayName ?? principal.PrincipalId.ToString())
        };
        claims.AddRange(principal.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(principal.Permissions.Select(p => new Claim(PermissionClaimType, p)));
        return new ClaimsIdentity(claims, AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            var principal = await core.GetCurrentPrincipalAsync();
            return new AuthenticationState(new ClaimsPrincipal(IdentityOf(principal)));
        }
        catch (CoreApiException)
        {
            // Not signed in (401) or Core unreachable: present as anonymous.
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }
    }
}
