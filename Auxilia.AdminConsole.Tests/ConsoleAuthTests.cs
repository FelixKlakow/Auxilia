using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Auxilia.AdminConsole.Auth;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Microsoft.AspNetCore.Http;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The same-origin bearer handoff: <see cref="ConsoleCallerTokenProvider"/> exchanges the operator's
/// Core session cookie for a per-user bearer via <c>POST /auth/token</c>, and the Core client's
/// <see cref="CoreCallerTokenHandler"/> attaches that bearer to outgoing requests.
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
            var body = new UserBearerToken("auxu_minted", DateTimeOffset.UtcNow.AddMinutes(10));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
                }
            };
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
        // Prerender relays the minted token to the circuit side.
        Assert.That(relay.Snapshot?.Invoke()?.Token, Is.EqualTo("auxu_minted"),
            "the minted token is offered to the prerender→circuit relay");
    }

    [Test]
    public async Task TokenProvider_YieldsRelayedToken_InInteractiveScope()
    {
        // Simulate the interactive circuit: no HttpContext (so no cookie to mint from), but the prerender
        // relayed a still-valid user bearer. The provider must yield it rather than fall back to the app key.
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var relay = new FakeRelay
        {
            Relayed = new UserBearerToken("auxu_relayed", DateTimeOffset.UtcNow.AddMinutes(10))
        };

        var provider = new ConsoleCallerTokenProvider(AccessorWithCookie(null), FactoryFor(stub), relay);

        var token = await provider.GetTokenAsync();

        Assert.Multiple(() =>
        {
            Assert.That(token, Is.EqualTo("auxu_relayed"), "the relayed user token is used during interactive rendering");
            Assert.That(stub.Calls, Is.EqualTo(0), "no cookie exchange is attempted (there is no HttpContext)");
        });
    }

    [Test]
    public async Task TokenProvider_FallsBackToAppKey_WhenRelayedTokenExpired()
    {
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var relay = new FakeRelay
        {
            // Already expired: not usable — the Core client should fall back to the static app key.
            Relayed = new UserBearerToken("auxu_stale", DateTimeOffset.UtcNow.AddMinutes(-1))
        };

        var provider = new ConsoleCallerTokenProvider(AccessorWithCookie(null), FactoryFor(stub), relay);

        Assert.That(await provider.GetTokenAsync(), Is.Null, "an expired relayed token yields null → app-key fallback");
    }

    [Test]
    public async Task TokenProvider_ReturnsNull_WhenNoSessionCookie()
    {
        var stub = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var provider = new ConsoleCallerTokenProvider(AccessorWithCookie(null), FactoryFor(stub), new FakeRelay());

        var token = await provider.GetTokenAsync();

        Assert.Multiple(() =>
        {
            Assert.That(token, Is.Null, "no session → fall back to the app key / anonymous");
            Assert.That(stub.Calls, Is.EqualTo(0), "no token exchange is attempted");
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

    private static IHttpContextAccessor AccessorWithCookie(string? cookieHeader)
    {
        var ctx = new DefaultHttpContext();
        if (cookieHeader is not null)
            ctx.Request.Headers.Cookie = cookieHeader;
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IHttpClientFactory FactoryFor(HttpMessageHandler handler)
        => new StubHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://core.local") });

    private sealed class FixedTokenProvider(string token) : ICoreCallerTokenProvider
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => new(token);
    }

    private sealed class FakeRelay : IUserBearerRelay
    {
        public UserBearerToken? Relayed { get; set; }
        public Func<UserBearerToken?>? Snapshot { get; private set; }

        public void OnPersist(Func<UserBearerToken?> snapshot) => Snapshot = snapshot;
        public UserBearerToken? TryTake() => Relayed;
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
