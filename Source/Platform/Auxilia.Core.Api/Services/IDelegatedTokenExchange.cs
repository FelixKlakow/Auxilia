namespace Auxilia.Core.Api.Services;

/// <summary>
/// Exchanges a user's access token for a downstream token scoped to a resource, on-behalf-of the
/// user (OAuth 2.0 OBO). Returns null when the exchange cannot be performed, so a delegated slot
/// fails closed. The default implementation is a no-op used when delegation is not configured; the
/// Entra implementation performs the real exchange.
/// </summary>
public interface IDelegatedTokenExchange
{
    Task<string?> ExchangeAsync(string userAccessToken, string resource, CancellationToken ct = default);
}

/// <summary>No-op: delegation not configured — every exchange yields nothing (the slot fails closed).</summary>
public sealed class NullDelegatedTokenExchange : IDelegatedTokenExchange
{
    public Task<string?> ExchangeAsync(string userAccessToken, string resource, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
