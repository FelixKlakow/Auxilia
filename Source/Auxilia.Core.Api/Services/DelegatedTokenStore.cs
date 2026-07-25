using Auxilia.Core.Api.Data;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Retains a signed-in user's access token, encrypted at rest, for the lifetime of that token — the
/// only stored secret the OBO delegation path needs. Keyed by principal, refreshed at each sign-in,
/// and never returned by a read endpoint. No refresh token is kept, so delegation is session-lifetime.
/// </summary>
public sealed class DelegatedTokenStore(
    IDataAccess<DelegatedUserTokenRecord> store, ISettingsProtector protector, TimeProvider clock)
{
    public Task RetainAsync(Guid principalId, string accessToken, DateTimeOffset expiresUtc, CancellationToken ct)
        => store.SaveAsync(new DelegatedUserTokenRecord
        {
            Id = principalId,
            ProtectedAccessToken = protector.Protect(accessToken),
            ExpiresUtc = expiresUtc
        }, ct);

    /// <summary>The retained token, or null when absent or expired (an expired token is purged).</summary>
    public async Task<string?> GetAsync(Guid principalId, CancellationToken ct)
    {
        if (await store.ReadAsync(principalId, ct) is not { } record)
            return null;
        if (record.ExpiresUtc <= clock.GetUtcNow())
        {
            await store.RemoveAsync(principalId, ct);
            return null;
        }
        return protector.Unprotect(record.ProtectedAccessToken);
    }
}
