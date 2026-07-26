using System.Net.Http.Headers;

namespace Auxilia.Core.Client;

/// <summary>
/// Supplies the per-caller bearer token for the current request — typically the Core-minted per-user
/// token of the signed-in console user. Returning null falls the request back to the app's static API
/// key, so existing service callers are unaffected.
/// </summary>
public interface ICoreCallerTokenProvider
{
    ValueTask<string?> GetTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// Sets <c>Authorization: Bearer &lt;token&gt;</c> per request from an <see cref="ICoreCallerTokenProvider"/>,
/// so a single <see cref="ICoreClient"/> can act AS whichever user is behind the current request. When
/// the provider yields no token, an optional static API key is used instead (unchanged service-caller path).
/// </summary>
public sealed class CoreCallerTokenHandler(ICoreCallerTokenProvider tokenProvider, string? fallbackApiKey = null)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        else if (!string.IsNullOrEmpty(fallbackApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fallbackApiKey);

        return await base.SendAsync(request, cancellationToken);
    }
}
