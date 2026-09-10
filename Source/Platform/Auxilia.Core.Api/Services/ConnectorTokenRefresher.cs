using System.Text.Json;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Keeps OAuth-backed connectors FRESH at the moment their settings are resolved: when the
/// provider's catalog entry declares a <see cref="ProviderOAuthRefresh"/> spec and the stored
/// access token is (or may be) stale, the refresh token is exchanged at the declared endpoint,
/// the rotated values are persisted, and only fresh settings leave. Entirely data-driven — the
/// Core never knows WHICH provider it is refreshing. A failed refresh returns the stored
/// settings so the downstream call produces the real error.
/// </summary>
public sealed class ConnectorTokenRefresher(
    ConnectorService connectors,
    ProviderCatalogService catalog,
    IHttpClientFactory httpClientFactory,
    TimeProvider clock,
    ILogger<ConnectorTokenRefresher> logger)
{
    /// <summary>Access tokens this close to expiry are refreshed proactively.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    // SINGLE-FLIGHT per connector: providers ROTATE refresh tokens, so two concurrent refreshes
    // of one connector (several runs resolving the same slot at once) would each spend the same
    // refresh token — the loser's rotated token then overwrites the winner's and the connector is
    // dead until re-connected. The second caller waits, re-reads, and finds the token fresh.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _refreshFlights = new();

    /// <summary>The connector's decrypted settings, refreshed first when the spec says so.</summary>
    public async Task<IReadOnlyDictionary<string, string>?> ResolveFreshSettingsAsync(
        Guid connectorId, CancellationToken ct)
    {
        var settings = await connectors.ResolveSettingsAsync(connectorId, ct);
        if (settings is null)
            return null;
        if (await connectors.GetAsync(connectorId, ct) is not { } connector
            || (await catalog.FindAsync(connector.ProviderType, ct))?.OAuthRefresh is not { } spec
            || !HasRefreshToken(settings, spec))
            return settings;
        if (IsFresh(settings, spec))
            return settings;

        var flight = _refreshFlights.GetOrAdd(connectorId, _ => new SemaphoreSlim(1, 1));
        await flight.WaitAsync(ct);
        try
        {
            // Re-read after acquiring: a concurrent caller may have refreshed (and rotated the
            // refresh token) while we waited — its result is what we must deliver, not a second
            // exchange of the now-spent token.
            settings = await connectors.ResolveSettingsAsync(connectorId, ct);
            if (settings is null)
                return null;
            if (!HasRefreshToken(settings, spec) || IsFresh(settings, spec))
                return settings;
            return await RefreshAsync(connectorId, connector.ProviderType, spec, settings, ct);
        }
        finally { flight.Release(); }
    }

    private static bool HasRefreshToken(IReadOnlyDictionary<string, string> settings, ProviderOAuthRefresh spec)
        => settings.TryGetValue(spec.RefreshTokenKey, out var refreshToken)
           && !string.IsNullOrWhiteSpace(refreshToken);

    /// <summary>An unknown expiry counts as stale — better one refresh too many than a revoked-token failure mid-run.</summary>
    private bool IsFresh(IReadOnlyDictionary<string, string> settings, ProviderOAuthRefresh spec)
        => settings.TryGetValue(spec.ExpiresAtKey, out var expiresAtRaw)
           && long.TryParse(expiresAtRaw, out var expiresAtMs)
           && DateTimeOffset.FromUnixTimeMilliseconds(expiresAtMs) - clock.GetUtcNow() > ExpirySkew;

    private async Task<IReadOnlyDictionary<string, string>> RefreshAsync(
        Guid connectorId, string providerType, ProviderOAuthRefresh spec,
        IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        var refreshToken = settings[spec.RefreshTokenKey];
        try
        {
            var http = httpClientFactory.CreateClient("oauth-refresh");
            using var response = await http.PostAsJsonAsync(spec.TokenEndpoint, new
            {
                grant_type = "refresh_token",
                refresh_token = refreshToken,
                client_id = spec.ClientId,
            }, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "OAuth refresh for connector {ConnectorId} failed with {Status} — delivering the stored token.",
                    connectorId, (int)response.StatusCode);
                return settings;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var access)
                || access.GetString() is not { Length: > 0 } accessToken)
                return settings;

            var updated = new Dictionary<string, string> { [spec.AccessTokenKey] = accessToken };
            // Rotated refresh tokens (when the endpoint issues one) replace the stored one.
            if (root.TryGetProperty("refresh_token", out var rotated)
                && rotated.GetString() is { Length: > 0 } newRefresh)
                updated[spec.RefreshTokenKey] = newRefresh;
            if (root.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt64(out var seconds))
                updated[spec.ExpiresAtKey] =
                    clock.GetUtcNow().AddSeconds(seconds).ToUnixTimeMilliseconds().ToString();

            await connectors.UpdateAsync(connectorId, new UpdateConnector(Settings: updated), ct);
            logger.LogInformation(
                "OAuth token refreshed for connector {ConnectorId} ({ProviderType}).",
                connectorId, providerType);

            var fresh = new Dictionary<string, string>(settings);
            foreach (var (key, value) in updated)
                fresh[key] = value;
            return fresh;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex,
                "OAuth refresh for connector {ConnectorId} errored — delivering the stored token.", connectorId);
            return settings;
        }
    }
}
