namespace Auxilia.Core.Contracts;

/// <summary>
/// The authenticated principal, as returned by <c>GET /auth/me</c>. <see cref="Permissions"/> is the
/// effective role-derived action set (the vocabulary of <c>policy</c> action identifiers) so clients
/// can show/hide functionality without knowing the role→permission matrix; per-workflow-type access
/// lists can still narrow an action at the point of use.
/// </summary>
public sealed record CurrentPrincipal(
    Guid PrincipalId,
    string? DisplayName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

/// <summary>
/// A short-lived, per-user bearer token minted by <c>POST /auth/token</c> for a delegated console
/// caller. Opaque to the client; sent as <c>Authorization: Bearer &lt;token&gt;</c> until <c>ExpiresUtc</c>.
/// </summary>
public sealed record UserBearerToken(string Token, DateTimeOffset ExpiresUtc);
