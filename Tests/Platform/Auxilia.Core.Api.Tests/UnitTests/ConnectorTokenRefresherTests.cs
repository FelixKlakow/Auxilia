using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.PlatformData.Protection;
using Auxilia.UniversalDataAccess.Implementations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class ConnectorTokenRefresherTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private ManualTimeProvider _time = null!;
    private ConnectorService _connectors = null!;
    private ProviderCatalogService _catalog = null!;
    private int _refreshCalls;
    private ConnectorTokenRefresher _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _time = new ManualTimeProvider();
        _refreshCalls = 0;
        _connectors = new ConnectorService(
            new InMemoryDataAccess<Auxilia.Core.Api.Data.CoreConnectorRecord>(),
            new AesGcmSettingsProtector(RandomNumberGenerator.GetBytes(32)),
            new AccessGrantEvaluator(
                new InMemoryDataAccess<PrincipalRecord>(), new InMemoryDataAccess<GroupMembershipRecord>()),
            _time);
        _catalog = new ProviderCatalogService(
            new InMemoryDataAccess<SlotProviderRecord>(),
            new InMemoryDataAccess<ProviderCatalogRecord>(),
            new AuditLog(new InMemoryDataAccess<AuditRecord>(), _time));
        await _catalog.RegisterAsync("test", new RegisterSlotProvider(
            "oauth-cli", "coding-agent", null, ["agent"], [],
            OAuthRefresh: new ProviderOAuthRefresh(
                "https://tokens.example/oauth/token", "client-1",
                "OAuthToken", "OAuthRefreshToken", "OAuthExpiresAt")), CancellationToken.None);

        var handler = new StubHttpMessageHandler(request =>
        {
            _refreshCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    access_token = "fresh-access",
                    refresh_token = "rotated-refresh",
                    expires_in = 3600,
                })),
            };
        });
        _sut = new ConnectorTokenRefresher(
            _connectors, _catalog, new StubHttpClientFactory(handler), _time,
            NullLogger<ConnectorTokenRefresher>.Instance);
    }

    private async Task<Guid> SeedConnectorAsync(Dictionary<string, string> settings, string provider = "oauth-cli")
        => (await _connectors.CreateAsync(
            new CreateConnector("c", provider, settings), null, CancellationToken.None)).Id;

    [Test]
    public async Task StaleToken_IsRefreshed_PersistedAndDelivered()
    {
        var id = await SeedConnectorAsync(new Dictionary<string, string>
        {
            ["OAuthToken"] = "stale-access",
            ["OAuthRefreshToken"] = "refresh-1",
            ["OAuthExpiresAt"] = _time.Now.AddMinutes(-5).ToUnixTimeMilliseconds().ToString(),
        });

        var delivered = await _sut.ResolveFreshSettingsAsync(id, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(delivered!["OAuthToken"], Is.EqualTo("fresh-access"));
            Assert.That(_refreshCalls, Is.EqualTo(1));
        });
        var persisted = await _connectors.ResolveSettingsAsync(id, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(persisted!["OAuthToken"], Is.EqualTo("fresh-access"),
                "the rotated access token must be persisted");
            Assert.That(persisted["OAuthRefreshToken"], Is.EqualTo("rotated-refresh"),
                "a rotated refresh token replaces the stored one");
            Assert.That(long.Parse(persisted["OAuthExpiresAt"]),
                Is.EqualTo(_time.Now.AddSeconds(3600).ToUnixTimeMilliseconds()));
        });
    }

    [Test]
    public async Task MissingExpiry_CountsAsStale_AndRefreshes()
    {
        var id = await SeedConnectorAsync(new Dictionary<string, string>
        {
            ["OAuthToken"] = "unknown-age",
            ["OAuthRefreshToken"] = "refresh-1",
        });

        var delivered = await _sut.ResolveFreshSettingsAsync(id, CancellationToken.None);

        Assert.That(delivered!["OAuthToken"], Is.EqualTo("fresh-access"));
    }

    [Test]
    public async Task FreshToken_IsDeliveredWithoutARefresh()
    {
        var id = await SeedConnectorAsync(new Dictionary<string, string>
        {
            ["OAuthToken"] = "still-good",
            ["OAuthRefreshToken"] = "refresh-1",
            ["OAuthExpiresAt"] = _time.Now.AddHours(2).ToUnixTimeMilliseconds().ToString(),
        });

        var delivered = await _sut.ResolveFreshSettingsAsync(id, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(delivered!["OAuthToken"], Is.EqualTo("still-good"));
            Assert.That(_refreshCalls, Is.Zero);
        });
    }

    [Test]
    public async Task ProviderWithoutRefreshSpec_IsPassedThrough()
    {
        await _catalog.RegisterAsync("test", new RegisterSlotProvider(
            "plain-cli", "coding-agent", null, ["agent"], []), CancellationToken.None);
        var id = await SeedConnectorAsync(
            new Dictionary<string, string> { ["ApiKey"] = "k" }, provider: "plain-cli");

        var delivered = await _sut.ResolveFreshSettingsAsync(id, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(delivered!["ApiKey"], Is.EqualTo("k"));
            Assert.That(_refreshCalls, Is.Zero);
        });
    }

    [Test]
    public async Task ConcurrentResolves_OfOneStaleConnector_RefreshOnce_AndBothGetTheRotatedToken()
    {
        // Providers rotate refresh tokens: two concurrent exchanges of the same refresh token
        // would leave the connector with whichever rotated token landed last — one of them
        // already revoked. The refresh is single-flight per connector; the waiter re-reads.
        var id = await SeedConnectorAsync(new Dictionary<string, string>
        {
            ["OAuthToken"] = "stale-access",
            ["OAuthRefreshToken"] = "refresh-1",
            ["OAuthExpiresAt"] = _time.Now.AddMinutes(-5).ToUnixTimeMilliseconds().ToString(),
        });

        var calls = 0;
        var firstCallEntered = new TaskCompletionSource();
        var release = new ManualResetEventSlim(false);
        var handler = new StubHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            firstCallEntered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    access_token = "fresh-access",
                    refresh_token = "rotated-refresh",
                    expires_in = 3600,
                })),
            };
        });
        var sut = new ConnectorTokenRefresher(
            _connectors, _catalog, new StubHttpClientFactory(handler), _time,
            NullLogger<ConnectorTokenRefresher>.Instance);

        var first = Task.Run(() => sut.ResolveFreshSettingsAsync(id, CancellationToken.None));
        await firstCallEntered.Task;
        var second = Task.Run(() => sut.ResolveFreshSettingsAsync(id, CancellationToken.None));
        // Give the second caller time to reach the token endpoint if nothing holds it back.
        await Task.Delay(300);
        release.Set();
        var delivered = await Task.WhenAll(first, second);

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1), "exactly one exchange of the refresh token");
            Assert.That(delivered.Select(d => d!["OAuthToken"]), Is.All.EqualTo("fresh-access"));
            Assert.That(delivered.Select(d => d!["OAuthRefreshToken"]), Is.All.EqualTo("rotated-refresh"));
        });
    }
}
