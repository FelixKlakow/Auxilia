using Auxilia.Core.Api.Services;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class RunnerLivenessTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void DeadSince_ReturnsRunnersWhoseLastBeatIsOlderThanCutoff()
    {
        var tracker = new RunnerLivenessTracker();
        var stale = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        tracker.Record(stale, T0);
        tracker.Record(fresh, T0 + TimeSpan.FromSeconds(40));

        var dead = tracker.DeadSince(T0 + TimeSpan.FromSeconds(30));

        Assert.That(dead, Is.EquivalentTo(new[] { stale }));
    }

    [Test]
    public void Record_KeepsTheLatestBeat_SoAReAppearingRunnerIsNotDead()
    {
        var tracker = new RunnerLivenessTracker();
        var id = Guid.NewGuid();
        tracker.Record(id, T0);
        tracker.Record(id, T0 + TimeSpan.FromSeconds(50));
        // An out-of-order older beat must not regress the last-seen time.
        tracker.Record(id, T0 + TimeSpan.FromSeconds(10));

        Assert.That(tracker.DeadSince(T0 + TimeSpan.FromSeconds(30)), Is.Empty);
    }

    [Test]
    public void Forget_DropsTheRunner_SoItsDeathTriggersFailoverAtMostOnce()
    {
        var tracker = new RunnerLivenessTracker();
        var id = Guid.NewGuid();
        tracker.Record(id, T0);

        Assert.That(tracker.DeadSince(T0 + TimeSpan.FromSeconds(30)), Is.EquivalentTo(new[] { id }));

        tracker.Forget(id);

        Assert.That(tracker.DeadSince(T0 + TimeSpan.FromSeconds(30)), Is.Empty);
    }
}
