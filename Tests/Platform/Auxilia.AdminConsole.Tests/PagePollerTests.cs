using Auxilia.AdminConsole.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The poll loop that never dies: raw transport exceptions (a Core restart) flip
/// <see cref="PagePoller.IsHealthy"/> and keep polling instead of killing the timer loop —
/// the exact failure mode that used to freeze the Dashboard/Runs pages permanently.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class PagePollerTests
{
    [Test]
    public async Task TickFailure_FlipsHealth_AndPollingContinues()
    {
        var calls = 0;
        var fail = true;
        using var poller = new PagePoller(TimeSpan.FromMilliseconds(10), _ =>
        {
            calls++;
            return fail ? throw new HttpRequestException("core down") : Task.CompletedTask;
        }, NullLogger.Instance);

        poller.Start();
        await WaitUntilAsync(() => calls >= 3);
        Assert.Multiple(() =>
        {
            Assert.That(poller.IsHealthy, Is.False, "a failing tick must surface as unhealthy");
            Assert.That(poller.LastError, Does.Contain("core down"));
            Assert.That(calls, Is.GreaterThanOrEqualTo(3),
                "the loop survives every failure — this used to kill the PeriodicTimer loop for good");
        });

        fail = false;
        await poller.RefreshNowAsync();
        Assert.Multiple(() =>
        {
            Assert.That(poller.IsHealthy, Is.True, "one success snaps the poller back to healthy");
            Assert.That(poller.LastError, Is.Null);
            Assert.That(poller.LastSuccessUtc, Is.Not.Null);
        });
    }

    [Test]
    public async Task StateChanged_FiresPerTick_AndDisposeStopsTheLoop()
    {
        var events = 0;
        var poller = new PagePoller(TimeSpan.FromMilliseconds(10), _ => Task.CompletedTask, NullLogger.Instance);
        poller.StateChanged += () => events++;

        poller.Start();
        await WaitUntilAsync(() => events >= 2);

        poller.Dispose();
        var after = events;
        await Task.Delay(100);
        Assert.That(events, Is.LessThanOrEqualTo(after + 1), "dispose stops the loop");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
            await Task.Delay(10);
        Assert.That(condition(), Is.True, "condition not reached in time");
    }
}
