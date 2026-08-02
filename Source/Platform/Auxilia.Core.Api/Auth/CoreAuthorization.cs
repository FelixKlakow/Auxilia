using System.Security.Claims;
using Auxilia.Governance.Policy;
using Microsoft.AspNetCore.Http;

namespace Auxilia.Core.Api.Auth;

/// <summary>Shared endpoint authorization: evaluate an action for the caller through the Policy Engine.</summary>
public static class CoreAuthorization
{
    /// <summary>Returns <c>null</c> when the action is allowed, otherwise a 401/403 result.</summary>
    public static async Task<IResult?> AuthorizeAsync(
        ClaimsPrincipal user, IPolicyEngine policy, string action, CancellationToken ct)
    {
        if (CoreClaims.PrincipalIdOf(user) is not { } principalId)
            return Results.Unauthorized();
        var decision = await policy.EvaluateAsync(new PolicyContext(principalId, action, action), ct);
        return decision.Allowed
            ? null
            : Results.Json(new { error = decision.Reason }, statusCode: StatusCodes.Status403Forbidden);
    }
}
