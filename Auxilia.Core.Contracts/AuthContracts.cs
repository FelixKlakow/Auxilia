namespace Auxilia.Core.Contracts;

/// <summary>The authenticated principal, as returned by <c>GET /auth/me</c>.</summary>
public sealed record CurrentPrincipal(Guid PrincipalId, string? DisplayName, IReadOnlyList<string> Roles);

/// <summary>
/// A short-lived, per-user bearer token minted by <c>POST /auth/token</c> for a delegated console
/// caller. Opaque to the client; sent as <c>Authorization: Bearer &lt;token&gt;</c> until <c>ExpiresUtc</c>.
/// </summary>
public sealed record UserBearerToken(string Token, DateTimeOffset ExpiresUtc);
