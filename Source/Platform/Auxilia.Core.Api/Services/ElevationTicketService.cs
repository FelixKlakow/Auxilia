using System.Security.Cryptography;
using System.Text;
using Auxilia.PlatformData;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Short-lived step-up elevations for security-sensitive administration (granting/revoking
/// administrator rights, disabling principals): minted by <c>POST /auth/step-up</c> after the
/// caller re-proves their own credential, presented as the <c>X-Auxilia-Elevation</c> header,
/// and valid for a few minutes of work. Like terminal tickets they are SELF-VALIDATING
/// (HMAC over principal + expiry) so an elevation minted on one Core.Api node validates on
/// every other; the MAC key derives from the shared settings-protection key (dev falls back
/// to a per-process key).
/// </summary>
public sealed class ElevationTicketService(TimeProvider clock, PlatformDataSettings dataSettings)
{
    public const string HeaderName = "X-Auxilia-Elevation";

    /// <summary>The error detail clients key their re-authentication prompt on.</summary>
    public const string RequiredError = "elevation-required";

    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);

    private readonly byte[] _key = string.IsNullOrWhiteSpace(dataSettings.ProtectionKeyBase64)
        ? RandomNumberGenerator.GetBytes(32)
        : SHA256.HashData([
            .. Convert.FromBase64String(dataSettings.ProtectionKeyBase64),
            .. "auxilia-elevation-ticket"u8
        ]);

    public (string Token, DateTimeOffset ExpiresUtc) Issue(Guid principalId)
    {
        var expires = clock.GetUtcNow() + TimeToLive;
        var payload = PayloadFor(principalId, expires.UtcTicks);
        return ($"{payload}.{MacFor(payload)}", expires);
    }

    /// <summary>True when the token is authentic, unexpired, and was minted for THIS principal.</summary>
    public bool Validate(string? token, Guid principalId)
    {
        if (token?.Split('.') is not [var principal, var ticksRaw, var mac])
            return false;
        if (!string.Equals(principal, principalId.ToString("N"), StringComparison.Ordinal))
            return false;
        if (!long.TryParse(ticksRaw, out var ticks)
            || new DateTimeOffset(ticks, TimeSpan.Zero) <= clock.GetUtcNow())
            return false;

        var expected = MacFor(PayloadFor(principalId, ticks));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(mac));
    }

    private static string PayloadFor(Guid principalId, long expiryTicks)
        => $"{principalId:N}.{expiryTicks}";

    private string MacFor(string payload)
        => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(payload)));
}
