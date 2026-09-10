using Auxilia.AdminConsole.Auth;
using Auxilia.Core.Contracts;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The server-side one-shot handle store behind the prerender → circuit session relay: a stash
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

    private static RelayedUserSession Session(FakeTimeProvider clock, TimeSpan? lifetime = null)
        => new(new UserBearerToken("auxu_secret", clock.GetUtcNow() + (lifetime ?? TimeSpan.FromMinutes(30))), "auxilia.core.session=cookie-secret");

    [Test]
    public void Redeem_ReturnsTheStashedToken_ExactlyOnce()
    {
        var clock = new FakeTimeProvider();
        var store = new UserBearerHandleStore(clock);
        var session = Session(clock);

        var handle = store.Stash(session);

        Assert.Multiple(() =>
        {
            Assert.That(handle, Does.Not.Contain("auxu_").And.Not.Contain("cookie-secret"), "the handle carries neither token nor cookie material");
            Assert.That(store.Redeem(handle), Is.EqualTo(session), "the first redeem recovers the session (token + cookie)");
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
        var handle = store.Stash(Session(clock));

        clock.Advance(UserBearerHandleStore.HandleLifetime + TimeSpan.FromSeconds(1));

        Assert.That(store.Redeem(handle), Is.Null, "a stale prerender handle is worthless");
    }

    [Test]
    public void Handle_NeverOutlivesTheTokenItProtects()
    {
        var clock = new FakeTimeProvider();
        var store = new UserBearerHandleStore(clock);
        // Token expires BEFORE the usual handle lifetime — the shorter expiry must win.
        var handle = store.Stash(Session(clock, lifetime: TimeSpan.FromSeconds(30)));

        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.That(store.Redeem(handle), Is.Null, "an expired token is not handed out");
    }

    [Test]
    public void Handles_AreUniquePerStash()
    {
        var clock = new FakeTimeProvider();
        var store = new UserBearerHandleStore(clock);
        var session = Session(clock);

        var first = store.Stash(session);
        var second = store.Stash(session);

        Assert.That(first, Is.Not.EqualTo(second), "every stash mints a fresh random handle");
    }
}
