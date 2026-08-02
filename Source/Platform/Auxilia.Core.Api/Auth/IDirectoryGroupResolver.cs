namespace Auxilia.Core.Api.Auth;

/// <summary>
/// Resolves a signed-in user's full directory group membership when the OIDC token signalled
/// group-claim overage (<see cref="CoreClaims.HasGroupOverage"/>). Entra caps the <c>groups</c> claim
/// at ~200 memberships and points at Microsoft Graph instead; this reads the real set back.
/// </summary>
public interface IDirectoryGroupResolver
{
    /// <param name="subject">The user's stable directory object id (for logging / app-only queries).</param>
    /// <param name="accessToken">The user's saved OIDC access token, used for the delegated Graph call.</param>
    Task<IReadOnlyList<string>> GetGroupIdsAsync(string subject, string? accessToken, CancellationToken ct = default);
}

/// <summary>
/// No-op fallback used when interactive sign-in is not configured: overage yields no additional
/// groups, so an over-quota user simply gets no directory-derived roles (deny-by-default) until a
/// Graph-backed resolver is wired.
/// </summary>
public sealed class NullDirectoryGroupResolver : IDirectoryGroupResolver
{
    public Task<IReadOnlyList<string>> GetGroupIdsAsync(string subject, string? accessToken, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>([]);
}
