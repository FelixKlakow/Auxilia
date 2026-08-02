using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// A signed-in user's Entra access token, retained encrypted at rest for the lifetime of that token
/// so a workflow can act on-behalf-of the user (OBO). Keyed by the principal; refreshed at each
/// sign-in and never returned by a read endpoint. No refresh token is stored — when it expires the
/// user must sign in again for delegated access (session-lifetime delegation, Principle 3).
/// </summary>
public sealed record DelegatedUserTokenRecord : IEntity
{
    /// <summary>The principal id (the user this token belongs to).</summary>
    public Guid Id { get; init; }

    /// <summary>The user's access token, protected via <c>ISettingsProtector</c>.</summary>
    public required string ProtectedAccessToken { get; init; }

    /// <summary>When the retained token expires; past this it is treated as absent.</summary>
    public DateTimeOffset ExpiresUtc { get; init; }
}
