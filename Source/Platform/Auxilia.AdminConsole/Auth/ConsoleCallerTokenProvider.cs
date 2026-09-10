using System.Net;
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
/// start (via <see cref="IHttpContextAccessor"/>), exchanges it at Core <c>POST /auth/token</c> for a
/// short-lived <see cref="UserBearerToken"/>, caches it, and re-mints before expiry.
/// <para>
/// Circuit hardening: the cookie is only visible during prerender, and the interactive circuit runs in
/// a different DI scope with no <c>HttpContext</c>. The prerender therefore relays BOTH the minted
/// bearer and the cookie it was minted from through <see cref="IUserBearerRelay"/> (server-side, one-shot
/// handle), so the circuit keeps re-minting as the user for as long as the Core session lives. This
/// provider must be resolved from the <b>circuit</b> scope (see <c>Program.cs</c>).
/// </para>
/// <para>
/// There is NO service-key fallback on a user circuit: when the provider has no usable session it
/// yields null and the request goes out unauthenticated (the Core answers 401 → anonymous → sign-in).
/// A session the Core stops accepting mid-circuit is surfaced as <see cref="SessionExpired"/> so the
/// shell can tell the operator to reload — the console never quietly acts as the service principal.
/// </para>
/// </summary>
public sealed class ConsoleCallerTokenProvider : ICoreCallerTokenProvider
{
    /// <summary>Re-mint this far ahead of expiry so an in-flight request never rides a just-expired token.</summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUserBearerRelay _relay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Captured at prerender while the HttpContext (and thus the browser's cookies) is live, or taken
    // from the relay on the circuit side.
    private string? _sessionCookieHeader;

    private UserBearerToken? _cached;
    private bool _relayChecked;
    private bool _sessionExpired;

    public ConsoleCallerTokenProvider(
        IHttpContextAccessor httpContextAccessor,
        IHttpClientFactory httpClientFactory,
        IUserBearerRelay relay)
    {
        _httpClientFactory = httpClientFactory;
        _relay = relay;
        var cookie = httpContextAccessor.HttpContext?.Request.Headers.Cookie.ToString();
        _sessionCookieHeader = string.IsNullOrEmpty(cookie) ? null : cookie;

        // Prerender side: hand whatever we minted (plus its cookie) to the circuit side before this scope ends.
        _relay.OnPersist(() => _cached is { } token && _sessionCookieHeader is { } header
            ? new RelayedUserSession(token, header)
            : null);
    }

    /// <summary>
    /// True once the operator HAD a session on this circuit and the Core stopped accepting it (the
    /// cookie session ended). Clears again if a later mint succeeds. Never true for a circuit that
    /// never had a session — that is the plain anonymous case, handled by the sign-in redirect.
    /// </summary>
    public bool SessionExpired
    {
        get => _sessionExpired;
        private set
        {
            if (_sessionExpired == value)
                return;
            _sessionExpired = value;
            SessionChanged?.Invoke();
        }
    }

    /// <summary>Raised when <see cref="SessionExpired"/> flips, so the shell can re-render its banner.</summary>
    public event Action? SessionChanged;

    public async ValueTask<string?> GetTokenAsync(CancellationToken ct = default)
    {
        if (Fresh(_cached))
            return _cached!.Token;

        await _gate.WaitAsync(ct);
        try
        {
            if (Fresh(_cached))
                return _cached!.Token;

            // Circuit side: adopt the prerender's session (token + cookie) exactly once.
            TakeRelayed();
            if (Fresh(_cached))
                return _cached!.Token;

            // No session at all → anonymous; never an app key.
            if (_sessionCookieHeader is null)
                return null;

            _cached = await MintAsync(ct);
            SessionExpired = _cached is null;
            return _cached?.Token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TakeRelayed()
    {
        if (_relayChecked)
            return;
        _relayChecked = true;

        if (_relay.TryTake() is not { } relayed)
            return;
        _sessionCookieHeader ??= relayed.SessionCookieHeader;
        if (Fresh(relayed.Token))
            _cached = relayed.Token;
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
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null; // The cookie session is gone (expired / signed out / disabled): no user token.
        if (!response.IsSuccessStatusCode)
            throw new CoreApiException(response.StatusCode, null, "The Core refused to mint a user bearer.");

        return await response.Content.ReadFromJsonAsync<UserBearerToken>(ct);
    }

    /// <summary>Named client for the cookie→bearer exchange; separate from the bearer-carrying Core client.</summary>
    public const string CoreAuthHttpClientName = "core-auth";
}
