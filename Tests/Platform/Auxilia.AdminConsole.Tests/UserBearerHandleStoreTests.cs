using Auxilia.AdminConsole.Auth;
using Auxilia.Core.Contracts;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The server-side one-shot handle store behind the prerender → circuit bearer relay: a stash
/// yields a random single-use handle, redeem is exactly-once, unknown handles yield nothing, and
/// an unredeemed entry expires quickly — never outliving the token it protects.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class UserBearerHandleStoreTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static UserBearerToken Token(FakeTimeProvider clock, TimeSpan? lifetime = null)
        => new("auxu_secret", clock.GetUtcNow() + (lifetime ?? TimeSpan.FromMinutes(30)));

    [Test]
    public void Redeem_ReturnsTheStashedToken_ExactlyOnce()
    {
        var clock = new FakeTimeProvider();
        var store = new UserBearerHandleStore(clock);
        var token = Token(clock);

        var handle = store.Stash(token);

        Assert.Multiple(() =>
        {
            Assert.That(handle, Does.Not.Contain("auxu_"), "the handle carries no token material");
            Assert.That(store.Redeem(handle), Is.EqualTo(token), "the first redeem recovers the token");
            Assert.That(store.Redeem(handle), Is.Null, "a handle is single-use — the second redeem gets nothing");
        });
    }

    [Test]
    public void Redeem_UnknownOrEmptyHandle_YieldsNothing()
    {
        var store = new UserBearerHandleStore(new FakeTimeProvider());

        Assert.Multiple(() =>
        {
            Assert.That(store.Redeem("not-a-handle"), Is.Null);
            Assert.That(store.Redeem(null), Is.Null);
            Assert.That(store.Redeem(""), Is.Null);
        });
    }

    [Test]
    public void UnredeemedHandle_ExpiresAfterTheHandleLifetime()
    {
        var clock = new FakeTimeProvider();
        var store = new UserBearerHandleStore(clock);
        var handle = store.Stash(Token(clock));

        clock.Advance(UserBearerHandleStore.HandleLifetime + TimeSpan.FromSeconds(1));

        Assert.That(store.Redeem(handle), Is.Null, "a stale prerender handle is worthless");
    }

    [Test]
    public void Handle_NeverOutlivesTheTokenItProtects()
    {
        var clock = new FakeTimeProvider();
        var store = new UserBearerHandleStore(clock);
        // Token expires BEFORE the usual handle lifetime — the shorter expiry must win.
        var handle = store.Stash(Token(clock, lifetime: TimeSpan.FromSeconds(30)));

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.That(store.Redeem(handle), Is.Null, "an expired token is not handed out");
    }

    [Test]
    public void Handles_AreUniquePerStash()
    {
        var clock = new FakeTimeProvider();
        var store = new UserBearerHandleStore(clock);
        var token = Token(clock);

        var first = store.Stash(token);
        var second = store.Stash(token);

        Assert.That(first, Is.Not.EqualTo(second), "every stash mints a fresh random handle");
    }
}
