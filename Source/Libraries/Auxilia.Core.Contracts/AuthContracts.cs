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

/// <summary>
/// Non-browser (desktop/CLI) sign-in (<c>POST /auth/login</c>): a human principal's username and
/// password exchanged for a per-user bearer — the same token shape the browser path mints, so
/// per-user audit and separation of duties apply to desktop clients too.
/// </summary>
public sealed record PasswordLoginRequest(string Username, string Password);

/// <summary>
/// Step-up re-authentication (<c>POST /auth/step-up</c>): the caller re-proves their OWN
/// credential — password for humans, API key for service principals — to obtain a short-lived
/// elevation for security-sensitive administration (granting/revoking administrator rights,
/// disabling principals).
/// </summary>
public sealed record StepUpRequest(string Secret);

/// <summary>
/// The elevation minted by a successful step-up: sent as the <c>X-Auxilia-Elevation</c> header
/// and valid for a few minutes of work. Endpoints that demand it reject other callers with the
/// error detail <c>elevation-required</c>.
/// </summary>
public sealed record ElevationTicket(string Token, DateTimeOffset ExpiresUtc);
