using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Short-lived bearer tickets for the terminal proxy. A browser (or WebView) cannot attach the
/// API bearer to ttyd's page, asset, and websocket requests, so an authorized caller first mints
/// a ticket and the proxy then accepts that ticket — bound to ONE run, expiring in minutes,
/// multi-use within its window (page + assets + websocket are separate requests).
/// </summary>
public sealed class TerminalTicketService(TimeProvider clock)
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, (Guid RunId, DateTimeOffset ExpiresUtc)> _tickets = new();

    public (string Ticket, DateTimeOffset ExpiresUtc) Issue(Guid runId)
    {
        Prune();
        var ticket = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expires = clock.GetUtcNow() + TimeToLive;
        _tickets[ticket] = (runId, expires);
        return (ticket, expires);
    }

    /// <summary>True when the ticket exists, is unexpired, and was minted for THIS run.</summary>
    public bool Validate(string? ticket, Guid runId)
        => ticket is { Length: > 0 }
           && _tickets.TryGetValue(ticket, out var entry)
           && entry.RunId == runId
           && entry.ExpiresUtc > clock.GetUtcNow();

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (key, value) in _tickets)
            if (value.ExpiresUtc <= now)
                _tickets.TryRemove(key, out _);
    }
}
