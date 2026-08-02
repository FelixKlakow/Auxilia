using System.Net.Http.Headers;
using System.Net.Http.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;

namespace Auxilia.AdminConsole.Auth;

/// <summary>
/// Supplies the per-user Core bearer for every <see cref="ICoreClient"/> call the console makes on
/// behalf of the signed-in operator. Same-origin bearer handoff (see the retirement plan's Deployment
/// note): the console and Core.Api sit behind one gateway, so the browser carries Core's
/// <c>auxilia.core.session</c> cookie to the console. This provider captures that cookie at circuit
/// start (via <see cref="IHttpContextAccessor"/>), exchanges it once at Core <c>POST /auth/token</c> for
/// a short-lived <see cref="UserBearerToken"/>, caches it, and re-mints before expiry.
/// <para>
/// Circuit hardening (3b-ii): the cookie is only visible during prerender, and the interactive circuit
/// runs in a different DI scope with no <c>HttpContext</c>. To keep acting as the user during interactive
/// rendering — rather than falling back to the app key — the minted bearer is relayed across the
/// prerender → circuit boundary through <see cref="IUserBearerRelay"/>. This provider must therefore be
/// resolved from the <b>circuit</b> scope (see <c>Program.cs</c>, which composes the Core client per scope
/// rather than through the pooled <c>HttpMessageHandler</c> scope). When neither a cookie nor a relayed
/// token is present it yields null, so the Core client falls back to the static app key.
/// </para>
/// </summary>
public sealed class ConsoleCallerTokenProvider : ICoreCallerTokenProvider
{
    /// <summary>Re-mint this far ahead of expiry so an in-flight request never rides a just-expired token.</summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUserBearerRelay _relay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Captured once at circuit start, while the HttpContext (and thus the browser's cookies) is live.
    private readonly string? _sessionCookieHeader;

    private UserBearerToken? _cached;
    private bool _relayChecked;

    public ConsoleCallerTokenProvider(
        IHttpContextAccessor httpContextAccessor,
        IHttpClientFactory httpClientFactory,
        IUserBearerRelay relay)
    {
        _httpClientFactory = httpClientFactory;
        _relay = relay;
        _sessionCookieHeader = httpContextAccessor.HttpContext?.Request.Headers.Cookie.ToString();

        // Prerender side: hand whatever we mint to the circuit side before this scope ends.
        _relay.OnPersist(() => _cached);
    }

    public async ValueTask<string?> GetTokenAsync(CancellationToken ct = default)
    {
        if (Fresh(_cached))
            return _cached!.Token;

        // Circuit side: no cookie to mint from, but the prerender may have relayed a live token.
        if (string.IsNullOrEmpty(_sessionCookieHeader))
            return TakeRelayed()?.Token;

        await _gate.WaitAsync(ct);
        try
        {
            if (Fresh(_cached))
                return _cached!.Token;

            _cached = await MintAsync(ct);
            return _cached?.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private UserBearerToken? TakeRelayed()
    {
        if (_relayChecked)
            return Fresh(_cached) ? _cached : null;

        _relayChecked = true;
        if (_relay.TryTake() is { } relayed && Fresh(relayed))
            _cached = relayed;
        return _cached;
    }

    private static bool Fresh(UserBearerToken? token)
        => token is not null && token.ExpiresUtc - DateTimeOffset.UtcNow > RenewBefore;

    private async Task<UserBearerToken?> MintAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/token");
        // Forward the operator's Core session cookie so /auth/token authenticates the cookie session
        // and mints a bearer bound to THAT principal (a service key can never self-issue a user token).
        request.Headers.TryAddWithoutValidation("Cookie", _sessionCookieHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var http = _httpClientFactory.CreateClient(CoreAuthHttpClientName);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null; // No live session (or not permitted): fall back to the static app key / anonymous.

        return await response.Content.ReadFromJsonAsync<UserBearerToken>(ct);
    }

    /// <summary>Named client for the cookie→bearer exchange; separate from the bearer-carrying Core client.</summary>
    public const string CoreAuthHttpClientName = "core-auth";
}
