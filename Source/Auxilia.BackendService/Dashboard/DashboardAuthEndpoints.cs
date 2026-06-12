using System.Security.Claims;
using Auxilia.Governance.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Auxilia.BackendService.Dashboard;

public sealed record LoginRequest(string Username, string Password);

/// <summary>
/// Cookie session endpoints over the platform identity provider. The session's
/// ClaimsPrincipal carries the principal ID and role names; every dashboard operation is
/// additionally authorized through the Policy Engine with that principal.
/// </summary>
public static class DashboardAuthEndpoints
{
    public const string PrincipalIdClaim = "auxilia:principal-id";

    public static void MapDashboardAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", async (
            LoginRequest request, IIdentityProvider identity, HttpContext http) =>
        {
            var session = await identity.AuthenticatePasswordAsync(request.Username, request.Password);
            if (session is null)
                return Results.Unauthorized();

            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                ToClaimsPrincipal(session));
            return Results.Ok(new { session.PrincipalId, session.DisplayName, session.Roles });
        });

        app.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });
    }

    public static ClaimsPrincipal ToClaimsPrincipal(IdentitySession session)
    {
        var claims = new List<Claim>
        {
            new(PrincipalIdClaim, session.PrincipalId.ToString("D")),
            new(ClaimTypes.Name, session.DisplayName)
        };
        claims.AddRange(session.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>The authenticated principal's ID, or null for anonymous callers.</summary>
    public static Guid? PrincipalIdOf(ClaimsPrincipal user)
        => Guid.TryParse(user.FindFirstValue(PrincipalIdClaim), out var id) ? id : null;
}
