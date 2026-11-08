using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.GitHubCopilot.Tests.UnitTests;

/// <summary>
/// A Copilot session runs on the token the slot's connector holds NOW: re-read while the run
/// goes on (spaced by the re-check interval), with a reported lifetime that makes the runtime
/// ask again about hourly — and within a minute once a declared expiry is in its last hour.
/// </summary>
[TestFixture, Category("Unit")]
public sealed class CopilotTokenSourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private ManualTime _time = null!;
    private Dictionary<string, string> _connector = null!;
    private int _reads;

    [SetUp]
    public void SetUp()
    {
        _time = new ManualTime(T0);
        _connector = new Dictionary<string, string> { ["token"] = "first" };
        _reads = 0;
    }

    private CopilotTokenSource Source(Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>>? reread = null)
        => new(CopilotCliOptions.FromSettings(_connector), reread ?? Reread, _time);

    private Task<IReadOnlyDictionary<string, string>> Reread(CancellationToken _)
    {
        _reads++;
        return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(_connector));
    }

    [Test]
    public async Task ATokenReplacedDuringTheRun_IsPickedUpAtTheNextRead()
    {
        var source = Source();
        Assert.That((await source.CurrentAsync()).Value, Is.EqualTo("first"));

        _connector["token"] = "replaced";
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.That((await source.CurrentAsync()).Value, Is.EqualTo("first"), "reads are spaced by the re-check interval");
        Assert.That(_reads, Is.Zero);

        _time.Advance(CopilotTokenSource.MinimumRecheck);
        Assert.That((await source.CurrentAsync()).Value, Is.EqualTo("replaced"));
        Assert.That(_reads, Is.EqualTo(1));
    }

    [TestCase(null, 120, Description = "no declared expiry: re-read about hourly")]
    [TestCase(600, 120, Description = "a long-lived token is still re-read about hourly")]
    [TestCase(90, 90, Description = "the runtime asks again when an hour is left")]
    [TestCase(30, 61, Description = "inside its last hour: asked for again at the next operation")]
    [TestCase(-5, 61, Description = "already expired: likewise, until a replacement arrives")]
    public async Task TheReportedLifetime_FollowsTheDeclaredExpiry_WithinTheRuntimesBounds(int? remainingMinutes, int reportedMinutes)
    {
        if (remainingMinutes is { } minutes)
            _connector[CopilotTokenSource.ExpiresAtKey] = T0.AddMinutes(minutes).ToString("O");

        var token = await Source().CurrentAsync();

        Assert.That(token.Lifetime, Is.EqualTo(TimeSpan.FromMinutes(reportedMinutes)));
    }

    [Test]
    public async Task AReplacementsDeclaredExpiry_TravelsWithIt()
    {
        _connector[CopilotTokenSource.ExpiresAtKey] = T0.AddMinutes(20).ToString("O");
        var source = Source();
        Assert.That((await source.CurrentAsync()).Lifetime, Is.EqualTo(CopilotTokenSource.MinimumReportedLifetime));

        _connector["token"] = "replaced";
        _connector[CopilotTokenSource.ExpiresAtKey] = T0.AddHours(8).ToUnixTimeSeconds().ToString();
        _time.Advance(CopilotTokenSource.MinimumRecheck);

        var replaced = await source.CurrentAsync();
        Assert.That(replaced.Value, Is.EqualTo("replaced"));
        Assert.That(replaced.Lifetime, Is.EqualTo(CopilotTokenSource.MaximumReportedLifetime));
    }

    [Test]
    public async Task AConnectorThatNoLongerHoldsAToken_EndsTheSession_InsteadOfRunningOnTheOldOne()
    {
        var source = Source();
        await source.CurrentAsync();
        _connector.Remove("token");
        _time.Advance(CopilotTokenSource.MinimumRecheck);

        var refusal = Assert.ThrowsAsync<InvalidOperationException>(() => source.CurrentAsync());
        Assert.That(refusal!.Message, Does.Not.Contain("first"), "never the token in a message");
    }

    [Test]
    public async Task AReadThePlatformDoesNotAnswer_KeepsTheCurrentToken_AndRetriesLater()
    {
        var failing = true;
        var source = Source(ct =>
        {
            _reads++;
            return failing
                ? Task.FromException<IReadOnlyDictionary<string, string>>(new TimeoutException("no activation response"))
                : Reread(ct);
        });
        _connector["token"] = "replaced";
        _time.Advance(CopilotTokenSource.MinimumRecheck);

        Assert.That((await source.CurrentAsync()).Value, Is.EqualTo("first"));
        Assert.That((await source.CurrentAsync()).Value, Is.EqualTo("first"), "no retry storm within the interval");
        Assert.That(_reads, Is.EqualTo(1));

        failing = false;
        _time.Advance(CopilotTokenSource.MinimumRecheck);
        Assert.That((await source.CurrentAsync()).Value, Is.EqualTo("replaced"));
    }

    [Test]
    public async Task WithoutAReread_TheRegisteredTokenStays()
    {
        var source = new CopilotTokenSource(CopilotCliOptions.FromSettings(_connector), time: _time);
        _time.Advance(TimeSpan.FromHours(3));
        Assert.That((await source.CurrentAsync()).Value, Is.EqualTo("first"));
    }

    [TestCase("2026-11-08T20:00:00Z")]
    [TestCase("1794168000")]
    [TestCase("1794168000000")]
    public void TheDeclaredExpiry_IsReadAsIsoOrUnixTime(string value)
        => Assert.That(CopilotCliOptions.ParseInstant(value), Is.EqualTo(new DateTimeOffset(2026, 11, 8, 20, 0, 0, TimeSpan.Zero)));

    // ------------------------------------------------------------------ the slot handler

    [TestCase("coding-agent")]
    [TestCase("review-agent")]
    public async Task EachCopilotSlot_RereadsItsOwnSlot_ThroughTheRunsSlotSource(string slotName)
    {
        var slots = new FakeSlotSource(new Dictionary<string, string> { ["token"] = "replaced" });
        var services = new ServiceCollection()
            .AddSingleton<ISlotConfigurationSource>(slots)
            .AddSingleton<TimeProvider>(_time);
        new CopilotCliSlotHandler().Register(services, slotName, typeof(ICodingAgent),
            new SlotConfiguration("github-copilot-cli", new Dictionary<string, string> { ["token"] = "first" }));
        await using var provider = services.BuildServiceProvider();

        var tokens = provider.GetRequiredKeyedService<CopilotTokenSource>(slotName);
        Assert.That((await tokens.CurrentAsync()).Value, Is.EqualTo("first"));
        _time.Advance(CopilotTokenSource.MinimumRecheck);

        Assert.That((await tokens.CurrentAsync()).Value, Is.EqualTo("replaced"));
        Assert.That(slots.Requested, Is.EqualTo(new[] { slotName }));
    }

    [Test]
    public async Task WithoutTheRunsSlotSource_TheHandlerKeepsTheRegisteredToken()
    {
        var services = new ServiceCollection();
        new CopilotCliSlotHandler().Register(services, "coding-agent", typeof(ICodingAgent),
            new SlotConfiguration("github-copilot-cli", new Dictionary<string, string> { ["token"] = "first" }));
        await using var provider = services.BuildServiceProvider();

        var tokens = provider.GetRequiredKeyedService<CopilotTokenSource>("coding-agent");
        Assert.That((await tokens.CurrentAsync()).Value, Is.EqualTo("first"));
        Assert.That(provider.GetRequiredService<ICodingAgent>(), Is.Not.Null);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeSlotSource(IReadOnlyDictionary<string, string> settings) : ISlotConfigurationSource
    {
        public List<string> Requested { get; } = [];

        public Task<SlotConfiguration> FetchAsync(string slotName, CancellationToken ct = default)
        {
            Requested.Add(slotName);
            return Task.FromResult(new SlotConfiguration("github-copilot-cli", settings));
        }
    }
}
