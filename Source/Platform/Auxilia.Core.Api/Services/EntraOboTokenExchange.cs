using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Auxilia.Core.Api.Auth;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Real OBO exchange against Entra: POSTs the on-behalf-of grant (the user's token as the assertion)
/// to the tenant token endpoint and returns the downstream access token, scoped to the requested
/// resource. Uses the same app registration (client id/secret) as sign-in — the app must be
/// consented for the downstream API. Any failure returns null so the delegated slot fails closed.
/// </summary>
public sealed class EntraOboTokenExchange(
    IHttpClientFactory httpClientFactory,
    IOptions<OidcSettings> oidc,
    ILogger<EntraOboTokenExchange> logger) : IDelegatedTokenExchange
{
    public async Task<string?> ExchangeAsync(string userAccessToken, string resource, CancellationToken ct = default)
    {
        var settings = oidc.Value;
        if (string.IsNullOrEmpty(settings.Authority) || string.IsNullOrEmpty(settings.ClientId)
            || string.IsNullOrEmpty(settings.ClientSecret))
        {
            logger.LogWarning("OBO delegation requested but the Entra relying party is not fully configured.");
            return null;
        }

        var scope = resource.Contains("/.default", StringComparison.Ordinal) ? resource : $"{resource}/.default";
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["client_id"] = settings.ClientId,
            ["client_secret"] = settings.ClientSecret,
            ["assertion"] = userAccessToken,
            ["scope"] = scope,
            ["requested_token_use"] = "on_behalf_of"
        };

        try
        {
            using var client = httpClientFactory.CreateClient();
            using var response = await client.PostAsync(
                TokenEndpoint(settings.Authority), new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OBO token exchange for {Resource} returned {StatusCode}.",
                    resource, (int)response.StatusCode);
                return null;
            }
            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
            return payload?.AccessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "OBO token exchange for {Resource} failed.", resource);
            return null;
        }
    }

    /// <summary>Derives the v2.0 token endpoint (…/{tenant}/v2.0 → …/{tenant}/oauth2/v2.0/token).</summary>
    private static string TokenEndpoint(string authority)
    {
        var baseUrl = authority.TrimEnd('/');
        if (baseUrl.EndsWith("/v2.0", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl[..^"/v2.0".Length];
        return $"{baseUrl}/oauth2/v2.0/token";
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
}
