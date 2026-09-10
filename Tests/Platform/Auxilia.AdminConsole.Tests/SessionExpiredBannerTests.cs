using System.Net;
using Auxilia.AdminConsole.Auth;
using Auxilia.AdminConsole.Components.Layout;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The shell's session-expired banner: once the Core stops accepting the circuit's session, the layout
/// says so and offers a reload — the console never keeps working as a service principal. Hermetic.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class SessionExpiredBannerTests
{
    [Test]
    public async Task Layout_ShowsSessionExpiredBanner_WhenTheCoreRejectsTheSession()
    {
        var core = new FakeCoreClient();
        var provider = new ConsoleCallerTokenProvider(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new StubHttpClientFactory(new HttpClient(new RejectingHandler()) { BaseAddress = new Uri("https://core.local") }),
            new FakeRelay(new RelayedUserSession(
                new UserBearerToken("auxu_stale", DateTimeOffset.UtcNow.AddMinutes(-1)),
                "auxilia.core.session=abc")));
        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new LiveRunMonitor(core, NullLogger<LiveRunMonitor>.Instance));
        ctx.Services.AddSingleton(provider);
        ctx.AddAuthorization().SetAuthorized("operator");

        var cut = ctx.Render<MainLayout>();
        Assert.That(cut.Markup, Does.Not.Contain("session has expired"), "a live session shows no banner");

        // Some Core call on the circuit finds the relayed bearer stale and the re-mint refused.
        await provider.GetTokenAsync();

        cut.WaitForAssertion(() => Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("session has expired"), "the operator is told explicitly");
            Assert.That(cut.Markup, Does.Contain("Reload"), "with the one action that recovers");
        }));
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeRelay(RelayedUserSession relayed) : IUserBearerRelay
    {
        public void OnPersist(Func<RelayedUserSession?> snapshot) { }
        public RelayedUserSession? TryTake() => relayed;
    }
}
