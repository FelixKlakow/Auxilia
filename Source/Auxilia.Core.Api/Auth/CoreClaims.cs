using System.Security.Claims;
using Auxilia.Governance.Identity;

namespace Auxilia.Core.Api.Auth;

/// <summary>Maps an authenticated <see cref="IdentitySession"/> to/from a ClaimsPrincipal.</summary>
public static class CoreClaims
{
    public const string PrincipalIdClaim = "auxilia:principal-id";
    public const string AuthType = "CoreApiKey";

    public static ClaimsPrincipal ToClaimsPrincipal(IdentitySession session, string authenticationType = AuthType)
    {
        var claims = new List<Claim>
        {
            new(PrincipalIdClaim, session.PrincipalId.ToString("D")),
            new(ClaimTypes.Name, session.DisplayName)
        };
        claims.AddRange(session.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType));
    }

    /// <summary>The authenticated principal's id, or null for anonymous callers.</summary>
    public static Guid? PrincipalIdOf(ClaimsPrincipal? user)
        => user is not null && Guid.TryParse(user.FindFirstValue(PrincipalIdClaim), out var id) ? id : null;

    /// <summary>
    /// Projects the claims of a validated external identity (OIDC/Entra) onto an
    /// <see cref="ExternalIdentity"/>. Prefers Entra's stable <c>oid</c> as the subject, falling
    /// back to the standard <c>sub</c>/name-identifier claims.
    /// </summary>
    public static ExternalIdentity ExternalIdentityFromPrincipal(ClaimsPrincipal principal, string provider)
    {
        var subject = principal.FindFirstValue("oid")
                      ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? principal.FindFirstValue("sub")
                      ?? throw new InvalidOperationException("The external identity carries no subject claim.");
        var displayName = principal.FindFirstValue("name")
                          ?? principal.Identity?.Name
                          ?? subject;
        var email = principal.FindFirstValue(ClaimTypes.Email)
                    ?? principal.FindFirstValue("email")
                    ?? principal.FindFirstValue("preferred_username");
        var groups = principal.FindAll("groups").Select(c => c.Value).ToList();
        return new ExternalIdentity(provider, subject, displayName, email, groups);
    }

    /// <summary>
    /// True when the token signalled group-claim overage: Entra omits the <c>groups</c> claim once a
    /// user is in more than ~200 groups, emitting <c>hasgroups=true</c> (or a <c>_claim_names</c>
    /// pointer) instead — the full membership must then be read from Microsoft Graph.
    /// </summary>
    public static bool HasGroupOverage(ClaimsPrincipal principal)
        => string.Equals(principal.FindFirstValue("hasgroups"), "true", StringComparison.OrdinalIgnoreCase)
           || principal.FindFirst("_claim_names")?.Value.Contains("\"groups\"", StringComparison.Ordinal) == true;
}
