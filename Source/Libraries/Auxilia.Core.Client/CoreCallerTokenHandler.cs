using System.Net.Http.Headers;

namespace Auxilia.Core.Client;

/// <summary>
/// Supplies the per-caller bearer token for the current request — typically the Core-minted per-user
/// token of the signed-in console user. Returning null sends the request WITHOUT credentials (the Core
/// answers 401); a delegated client never degrades to a service key.
/// </summary>
public interface ICoreCallerTokenProvider
{
    ValueTask<string?> GetTokenAsync(CancellationToken ct = default);
}

/// <summary>
/// Sets <c>Authorization: Bearer &lt;token&gt;</c> per request from an <see cref="ICoreCallerTokenProvider"/>,
/// so a single <see cref="ICoreClient"/> can act AS whichever user is behind the current request. When
/// the provider yields no token the request carries no Authorization header at all — there is
/// deliberately no static-key fallback here: a client that should act as a service principal is built
/// with the app-key overload of <c>AddCoreClient</c>, never by letting a user client silently downgrade.
/// </summary>
public sealed class CoreCallerTokenHandler(ICoreCallerTokenProvider tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = string.IsNullOrEmpty(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
