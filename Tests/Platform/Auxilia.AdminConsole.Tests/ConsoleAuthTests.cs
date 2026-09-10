using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Auxilia.AdminConsole.Auth;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The same-origin bearer handoff: <see cref="ConsoleCallerTokenProvider"/> exchanges the operator's
/// Core session cookie for a per-user bearer via <c>POST /auth/token</c>, relays token + cookie to the
/// interactive circuit so it can re-mint on expiry, and the Core client's
/// <see cref="CoreCallerTokenHandler"/> attaches that bearer — and ONLY that bearer — to outgoing
/// requests. A user circuit never degrades to a service key.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class ConsoleAuthTests
{
    [Test]
    public async Task TokenProvider_ExchangesSessionCookie_ForBearer()
    {
        var stub = new StubHandler((req, _) =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(req.RequestUri!.AbsolutePath, Is.EqualTo("/auth/token"));
                Assert.That(req.Method, Is.EqualTo(HttpMethod.Post));
                Assert.That(req.Headers.GetValues("Cookie").Single(),
                    Does.Contain("auxilia.core.session=abc"), "the Core session cookie is forwarded");
            });
            return Minted("auxu_minted");
        });

        var relay = new FakeRelay();
        var provider = new ConsoleCallerTokenProvider(
            AccessorWithCookie("auxilia.core.session=abc"),
            FactoryFor(stub),
            relay);

        var token = await provider.GetTokenAsync();

        Assert.That(token, Is.EqualTo("auxu_minted"));
        // Second call is served from the cache — no second exchange.
        await provider.GetTokenAsync();
        Assert.That(stub.Calls, Is.EqualTo(1), "the minted token is cached for the circuit");
        // Prerender relays the minted token AND the cookie it came from to the circuit side.
        var relayed = relay.Snapshot?.Invoke();
        Assert.Multiple(() =>
        {
            Assert.That(relayed?.Token.Token, Is.EqualTo("auxu_minted"),
                "the minted token is offered to the prerender→circuit relay");
            Assert.That(relayed?.SessionCookieHeader, Is.EqualTo("auxilia.core.session=abc"),
                "the cookie travels with it so the circuit can re-mint");
        });
    }

    [Test]
    public async Task TokenProvider_YieldsRelayedToken_InInteractiveScope()
    {
        // Simulate the interactive circuit: no HttpContext (so no cookie to mint from), but the prerender
        // relayed a still-valid user bearer. The provider must yield it without a new exchange.
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var relay = new FakeRelay
        {
            Relayed = new RelayedUserSession(
                new UserBearerToken("auxu_relayed", DateTimeOffset.UtcNow.AddMinutes(10)),
                "auxilia.core.session=abc")
        };

        var provider = new ConsoleCallerTokenProvider(AccessorWithCookie(null), FactoryFor(stub), relay);

        var token = await provider.GetTokenAsync();

        Assert.Multiple(() =>
        {
            Assert.That(token, Is.EqualTo("auxu_relayed"), "the relayed user token is used during interactive rendering");
            Assert.That(stub.Calls, Is.EqualTo(0), "no cookie exchange is attempted while the relayed token is fresh");
        });
    }

    [Test]
    public async Task TokenProvider_RemintsFromRelayedCookie_WhenRelayedTokenExpired()
    {
        // The circuit outlived the 30-minute bearer: the provider re-mints from the relayed cookie
        // instead of yielding null (which used to mean "attach the service key").
        var stub = new StubHandler((req, _) =>
        {
            Assert.That(req.Headers.GetValues("Cookie").Single(), Is.EqualTo("auxilia.core.session=abc"),
                "the relayed cookie authenticates the re-mint");
            return Minted("auxu_fresh");
        });
        var relay = new FakeRelay
        {
            Relayed = new RelayedUserSession(
                new UserBearerToken("auxu_stale", DateTimeOffset.UtcNow.AddMinutes(-1)),
                "auxilia.core.session=abc")
        };

        var provider = new ConsoleCallerTokenProvider(AccessorWithCookie(null), FactoryFor(stub), relay);

        var token = await provider.GetTokenAsync();

        Assert.Multiple(() =>
        {
            Assert.That(token, Is.EqualTo("auxu_fresh"), "the circuit re-mints as the user");
            Assert.That(stub.Calls, Is.EqualTo(1));
            Assert.That(provider.SessionExpired, Is.False);
        });
    }

    [Test]
    public async Task TokenProvider_FlagsSessionExpired_WhenCoreRejectsRelayedCookie()
    {
        // The Core session behind the relayed cookie ended (sign-out / cookie expiry): no user token,
        // NO service key, and the shell is told so it can ask the operator to reload.
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var relay = new FakeRelay
        {
            Relayed = new RelayedUserSession(
                new UserBearerToken("auxu_stale", DateTimeOffset.UtcNow.AddMinutes(-1)),
                "auxilia.core.session=abc")
        };
        var provider = new ConsoleCallerTokenProvider(AccessorWithCookie(null), FactoryFor(stub), relay);
        var changes = 0;
        provider.SessionChanged += () => changes++;

        var token = await provider.GetTokenAsync();

        Assert.Multiple(() =>
        {
            Assert.That(token, Is.Null, "no user token → the request goes out unauthenticated");
            Assert.That(provider.SessionExpired, Is.True, "the circuit HAD a session and lost it");
            Assert.That(changes, Is.EqualTo(1), "the shell is notified exactly once per flip");
        });
    }

    [Test]
    public async Task TokenProvider_ReturnsNull_WhenNoSessionCookie()
    {
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = new ConsoleCallerTokenProvider(AccessorWithCookie(null), FactoryFor(stub), new FakeRelay());

        var token = await provider.GetTokenAsync();

        Assert.Multiple(() =>
        {
            Assert.That(token, Is.Null, "no session → anonymous (the sign-in redirect takes over)");
            Assert.That(stub.Calls, Is.EqualTo(0), "no token exchange is attempted");
            Assert.That(provider.SessionExpired, Is.False, "never-signed-in is not 'expired'");
        });
    }

    [Test]
    public async Task CallerTokenHandler_AttachesBearer_FromProvider()
    {
        var inner = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new CoreCallerTokenHandler(new FixedTokenProvider("auxu_xyz")) { InnerHandler = inner };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://core.local") };

        await client.GetAsync("/api/audit");

        Assert.That(inner.LastRequest!.Headers.Authorization, Is.EqualTo(
            new AuthenticationHeaderValue("Bearer", "auxu_xyz")));
    }

    [Test]
    public async Task CallerTokenHandler_SendsNoCredential_WhenProviderYieldsNothing()
    {
        // The delegated client has no static-key fallback: an absent user token means an
        // unauthenticated request (Core → 401), never a silent switch to the service principal.
        var inner = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new CoreCallerTokenHandler(new FixedTokenProvider(null)) { InnerHandler = inner };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://core.local") };
        // Even a pre-set header (e.g. a stale default) must not leak through.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "auxk_service");

        await client.GetAsync("/api/audit");

        Assert.That(inner.LastRequest!.Headers.Authorization, Is.Null, "no Authorization header at all");
    }

    [Test]
    public async Task AuthenticationStateProvider_YieldsAnonymous_WhenCoreUnreachable()
    {
        var core = new Mock<ICoreClient>();
        core.Setup(c => c.GetCurrentPrincipalAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var provider = new ConsoleAuthenticationStateProvider(core.Object,
            NullLogger<ConsoleAuthenticationStateProvider>.Instance);

        var state = await provider.GetAuthenticationStateAsync();

        Assert.That(state.User.Identity?.IsAuthenticated, Is.False,
            "an unreachable Core degrades the circuit to anonymous instead of crashing it");
    }

    private static HttpResponseMessage Minted(string token)
    {
        var body = new UserBearerToken(token, DateTimeOffset.UtcNow.AddMinutes(10));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
            }
        };
    }

    private static IHttpContextAccessor AccessorWithCookie(string? cookieHeader)
    {
        var ctx = new DefaultHttpContext();
        if (cookieHeader is not null)
            ctx.Request.Headers.Cookie = cookieHeader;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IHttpClientFactory FactoryFor(HttpMessageHandler handler)
        => new StubHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://core.local") });

    private sealed class FixedTokenProvider(string? token) : ICoreCallerTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => new(token);
    }

    private sealed class FakeRelay : IUserBearerRelay
    {
        public RelayedUserSession? Relayed { get; set; }
        public Func<RelayedUserSession?>? Snapshot { get; private set; }

        public void OnPersist(Func<RelayedUserSession?> snapshot) => Snapshot = snapshot;
        public RelayedUserSession? TryTake() => Relayed;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(respond(request, ct));
        }
    }
}
