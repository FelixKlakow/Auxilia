using Auxilia.Core.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.TriggerHost.Tests.ComponentTests;

/// <summary>
/// The host binds the WHOLE <c>Core:*</c> section onto <see cref="CoreClientOptions"/> — not just
/// the address and key — so the documented stream/unary knobs actually reach the client.
/// Observed through behaviour: a one-second <c>Core:UnaryTimeoutSeconds</c> must bound a call
/// against a Core that never answers.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class CoreClientOptionsBindingTests
{
    /// <summary>A Core that accepts the connection and never answers.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private WebApplicationFactory<Program> _factory = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Core:BaseAddress", "http://core.test/");
            builder.UseSetting("Core:UnaryTimeoutSeconds", "1");
            builder.UseSetting("PlatformData:Backend", "InMemory");
            builder.ConfigureTestServices(services =>
                services.ConfigureHttpClientDefaults(http =>
                    http.ConfigurePrimaryHttpMessageHandler(() => new HangingHandler())));
        });
    }

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public void ConfiguredUnaryTimeout_BoundsACallAgainstASilentCore()
    {
        var core = _factory.Services.GetRequiredService<ICoreClient>();
        // Well below the client's 100s default: only a BOUND Core:UnaryTimeoutSeconds gets here.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Assert.CatchAsync<TimeoutException>(async () => await core.GetRunStatsAsync(guard.Token),
            "Core:UnaryTimeoutSeconds is bound onto CoreClientOptions and surfaces as the client's timeout");
    }
}
