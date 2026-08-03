using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Auth;

/// <summary>
/// Mints and validates short-lived, per-user bearer tokens for delegated console callers. A token is
/// an opaque, DataProtection-protected (signed + encrypted) string that binds a principal id to an
/// absolute expiry — nothing else. It carries no roles or secrets: authorization re-resolves the
/// principal's roles server-side per request. The same DataProtection provider that protects the
/// session cookie signs these, so no new key management is introduced.
/// </summary>
public sealed class UserBearerTokenService
{
    /// <summary>Distinguishes a user bearer from an <c>aux_</c> API key at a glance (routing + hygiene).</summary>
    internal const string Prefix = "auxu_";
    private const string ProtectorPurpose = "Auxilia.Core.Api.UserBearer.v1";

    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _lifetime;

    public UserBearerTokenService(
        IDataProtectionProvider dataProtection, TimeProvider clock, IOptions<CoreSecuritySettings> settings)
    {
        _protector = dataProtection.CreateProtector(ProtectorPurpose);
        _clock = clock;
        _lifetime = TimeSpan.FromMinutes(Math.Max(1, settings.Value.UserTokenLifetimeMinutes));
    }

    /// <summary>
    /// Issues a token for the principal, expiring <paramref name="lifetime"/> (or the configured
    /// console lifetime) from now.
    /// </summary>
    public (string Token, DateTimeOffset ExpiresUtc) Issue(Guid principalId, TimeSpan? lifetime = null)
    {
        var expiresUtc = _clock.GetUtcNow().Add(lifetime ?? _lifetime);
        return (Protect(principalId, expiresUtc), expiresUtc);
    }

    /// <summary>
    /// Protects a token that binds <paramref name="principalId"/> to an explicit <paramref name="expiresUtc"/>.
    /// Used by <see cref="Issue"/>; also exposed so tests can mint already-expired tokens deterministically.
    /// </summary>
    public string Protect(Guid principalId, DateTimeOffset expiresUtc)
    {
        var payload = string.Create(CultureInfo.InvariantCulture,
            $"{principalId:D}|{expiresUtc.ToUnixTimeSeconds()}");
        return Prefix + _protector.Protect(payload);
    }

    /// <summary>
    /// Validates a token's shape, signature, and expiry. Returns the bound principal id, or null when
    /// the token is not a user bearer, is forged/tampered, or has expired. Does NOT verify the
    /// principal is still active — the caller resolves the session for that.
    /// </summary>
    public Guid? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token) || !token.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        string payload;
        try
        {
            payload = _protector.Unprotect(token[Prefix.Length..]);
        }
        catch (CryptographicException)
        {
            return null; // tampered, forged, or protected under a different key
        }

        var separator = payload.IndexOf('|');
        if (separator <= 0
            || !Guid.TryParse(payload.AsSpan(0, separator), out var principalId)
            || !long.TryParse(payload.AsSpan(separator + 1), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var expiresUnix))
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(expiresUnix) <= _clock.GetUtcNow() ? null : principalId;
    }
}
