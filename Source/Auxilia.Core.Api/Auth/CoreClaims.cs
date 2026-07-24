using System.Security.Claims;
using Auxilia.Governance.Identity;

namespace Auxilia.Core.Api.Auth;

/// <summary>Maps an authenticated <see cref="IdentitySession"/> to/from a ClaimsPrincipal.</summary>
public static class CoreClaims
{
    public const string PrincipalIdClaim = "auxilia:principal-id";
    public const string AuthType = "CoreApiKey";

    public static ClaimsPrincipal ToClaimsPrincipal(IdentitySession session)
    {
        var claims = new List<Claim>
        {
            new(PrincipalIdClaim, session.PrincipalId.ToString("D")),
            new(ClaimTypes.Name, session.DisplayName)
        };
        claims.AddRange(session.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthType));
    }

    /// <summary>The authenticated principal's id, or null for anonymous callers.</summary>
    public static Guid? PrincipalIdOf(ClaimsPrincipal? user)
        => user is not null && Guid.TryParse(user.FindFirstValue(PrincipalIdClaim), out var id) ? id : null;
}
