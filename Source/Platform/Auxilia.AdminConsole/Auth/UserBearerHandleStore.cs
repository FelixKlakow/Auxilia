using System.Collections.Concurrent;
using System.Security.Cryptography;
using Auxilia.Core.Contracts;

namespace Auxilia.AdminConsole.Auth;

/// <summary>
/// Server-side one-shot store for the per-user Core bearer crossing the prerender → circuit
/// boundary. The prerendered page carries only a cryptographically random SINGLE-USE handle —
/// never the token itself — so the raw bearer no longer appears in the page's persisted state.
/// A handle is redeemable exactly once (removed on redeem) and expires quickly; it never
/// outlives the token it protects. Singleton: the prerender request and the circuit are
/// different DI scopes, and the handoff must survive the scope change.
/// </summary>
public sealed class UserBearerHandleStore(TimeProvider clock)
{
    /// <summary>Unredeemed handles die fast — the circuit connects within seconds of prerender.</summary>
    internal static readonly TimeSpan HandleLifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, (UserBearerToken Token, DateTimeOffset ExpiresUtc)> _entries = new();

    /// <summary>Stores the token and returns the one-time handle to persist into the page state.</summary>
    public string Stash(UserBearerToken token)
    {
        SweepExpired();
        var handle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow();
        var expiresUtc = now + HandleLifetime < token.ExpiresUtc ? now + HandleLifetime : token.ExpiresUtc;
        _entries[handle] = (token, expiresUtc);
        return handle;
    }

    /// <summary>Redeems a handle exactly once; null for an unknown, already-redeemed, or expired one.</summary>
    public UserBearerToken? Redeem(string? handle)
    {
        if (string.IsNullOrEmpty(handle) || !_entries.TryRemove(handle, out var entry))
            return null;
        return entry.ExpiresUtc <= clock.GetUtcNow() ? null : entry.Token;
    }

    /// <summary>Drops unredeemed, expired entries (e.g. prerenders whose circuit never connected).</summary>
    private void SweepExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var (handle, entry) in _entries)
        {
            if (entry.ExpiresUtc <= now)
                _entries.TryRemove(handle, out _);
        }
    }
}
