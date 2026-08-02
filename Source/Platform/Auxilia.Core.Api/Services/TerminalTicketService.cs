using System.Security.Cryptography;
using System.Text;
using Auxilia.PlatformData;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Short-lived bearer tickets for the terminal proxy. A browser (or WebView) cannot attach the
/// API bearer to ttyd's page, asset, and websocket requests, so an authorized caller first mints
/// a ticket and the proxy then accepts that ticket — bound to ONE run, expiring in minutes,
/// multi-use within its window (page + assets + websocket are separate requests).
/// Tickets are SELF-VALIDATING (HMAC over run + expiry) instead of node-local state, so a
/// ticket minted on one Core.Api node validates on every other behind a load balancer. The MAC
/// key derives from the shared settings-protection key; without one (dev) a per-process key
/// keeps single-node behaviour.
/// </summary>
public sealed class TerminalTicketService(TimeProvider clock, PlatformDataSettings dataSettings)
{
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(2);

    private readonly byte[] _key = string.IsNullOrWhiteSpace(dataSettings.ProtectionKeyBase64)
        ? RandomNumberGenerator.GetBytes(32)
        : SHA256.HashData([
            .. Convert.FromBase64String(dataSettings.ProtectionKeyBase64),
            .. "auxilia-terminal-ticket"u8
        ]);

    public (string Ticket, DateTimeOffset ExpiresUtc) Issue(Guid runId)
    {
        var expires = clock.GetUtcNow() + TimeToLive;
        var payload = PayloadFor(runId, expires.UtcTicks);
        return ($"{payload}.{MacFor(payload)}", expires);
    }

    /// <summary>True when the ticket is authentic, unexpired, and was minted for THIS run.</summary>
    public bool Validate(string? ticket, Guid runId)
    {
        if (ticket?.Split('.') is not [var run, var ticksRaw, var mac])
            return false;
        if (!string.Equals(run, runId.ToString("N"), StringComparison.Ordinal))
            return false;
        if (!long.TryParse(ticksRaw, out var ticks)
            || new DateTimeOffset(ticks, TimeSpan.Zero) <= clock.GetUtcNow())
            return false;

        var expected = MacFor(PayloadFor(runId, ticks));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(mac));
    }

    private static string PayloadFor(Guid runId, long expiryTicks)
        => $"{runId:N}.{expiryTicks}";

    private string MacFor(string payload)
        => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));
}
