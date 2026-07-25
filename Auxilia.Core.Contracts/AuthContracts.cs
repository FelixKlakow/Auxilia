namespace Auxilia.Core.Contracts;

/// <summary>The authenticated principal, as returned by <c>GET /auth/me</c>.</summary>
public sealed record CurrentPrincipal(Guid PrincipalId, string? DisplayName, IReadOnlyList<string> Roles);
