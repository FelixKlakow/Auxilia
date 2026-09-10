using Auxilia.Core.Api.Auth;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// The per-username failed-attempt throttle behind POST /auth/login: blocks after the configured
/// failures within the sliding window, unblocks when the window slides past, and a successful
/// sign-in clears the username's slate. Usernames are independent and matched case-insensitively.
/// </summary>
[TestFixture, Category("Unit")]
public sealed class LoginAttemptThrottleTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static LoginAttemptThrottle Throttle(
        FakeTimeProvider clock, int limit = 3, int windowMinutes = 5)
        => new(clock, Options.Create(new CoreSecuritySettings
        {
            LoginFailureLimitPerUsername = limit,
            LoginFailureWindowMinutes = windowMinutes
        }));

    [Test]
    public void PrincipalKeys_ThrottleStepUp_IndependentlyOfUsernames()
    {
        var clock = new FakeTimeProvider();
        var throttle = Throttle(clock, limit: 2);
        var principal = Guid.NewGuid();

        throttle.RecordFailure(principal);
        Assert.That(throttle.IsBlocked(principal), Is.False, "one failure is still allowed");
        throttle.RecordFailure(principal);

        Assert.Multiple(() =>
        {
            Assert.That(throttle.IsBlocked(principal), Is.True, "step-up guessing is throttled per principal");
            Assert.That(throttle.IsBlocked(Guid.NewGuid()), Is.False, "another principal is not collaterally locked");
            Assert.That(throttle.IsBlocked(principal.ToString("D")), Is.False,
                "a username that happens to spell the guid lives in a different key space");
        });

        throttle.RecordSuccess(principal);
        Assert.That(throttle.IsBlocked(principal), Is.False, "a successful proof clears the slate");
    }

    [Test]
    public void Blocks_AfterTheConfiguredFailures_WithinTheWindow()
    {
        var clock = new FakeTimeProvider();
        var throttle = Throttle(clock, limit: 3);

        for (var i = 0; i < 3; i++)
        {
            Assert.That(throttle.IsBlocked("jane"), Is.False, $"attempt {i + 1} is still allowed");
            throttle.RecordFailure("jane");
        }

        Assert.That(throttle.IsBlocked("jane"), Is.True, "the limit is reached — further attempts are refused");
    }

    [Test]
    public void Unblocks_WhenTheWindowSlidesPast()
    {
        var clock = new FakeTimeProvider();
        var throttle = Throttle(clock, limit: 2, windowMinutes: 5);
        throttle.RecordFailure("jane");
        throttle.RecordFailure("jane");
        Assert.That(throttle.IsBlocked("jane"), Is.True);

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Assert.That(throttle.IsBlocked("jane"), Is.False, "aged-out failures no longer count");
    }

    [Test]
    public void SuccessfulSignIn_ClearsTheSlate()
    {
        var clock = new FakeTimeProvider();
        var throttle = Throttle(clock, limit: 2);
        throttle.RecordFailure("jane");
        throttle.RecordSuccess("jane");
        throttle.RecordFailure("jane");

        Assert.That(throttle.IsBlocked("jane"), Is.False,
            "only failures SINCE the last successful sign-in count");
    }

    [Test]
    public void Usernames_AreThrottledIndependently_ButCaseInsensitively()
    {
        var clock = new FakeTimeProvider();
        var throttle = Throttle(clock, limit: 2);
        throttle.RecordFailure("jane");
        throttle.RecordFailure("JANE");

        Assert.Multiple(() =>
        {
            Assert.That(throttle.IsBlocked("Jane"), Is.True, "casing does not dodge the throttle");
            Assert.That(throttle.IsBlocked("john"), Is.False, "another account is unaffected");
        });
    }
}
