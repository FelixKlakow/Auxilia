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
    public void Record_KeepsTheLastNonNullAdvertisement_AcrossBeats()
    {
        var tracker = new RunnerLivenessTracker();
        var id = Guid.NewGuid();
        // First beats arrive before the runner has probed its daemon — no platform yet.
        tracker.Record(id, T0, "Auxilia.Core.Runner");
        tracker.Record(id, T0 + TimeSpan.FromSeconds(5), "Auxilia.Core.Runner", "linux", "x86_64");
        // A later beat without a platform (fresh probe cache after restart) must not erase it.
        tracker.Record(id, T0 + TimeSpan.FromSeconds(10), "Auxilia.Core.Runner");

        var runner = tracker.Snapshot().Single();
        Assert.Multiple(() =>
        {
            Assert.That(runner.ServiceName, Is.EqualTo("Auxilia.Core.Runner"));
            Assert.That(runner.LastSeen, Is.EqualTo(T0 + TimeSpan.FromSeconds(10)));
            Assert.That(runner.HostPlatform, Is.EqualTo("linux"));
            Assert.That(runner.HostArchitecture, Is.EqualTo("x86_64"));
        });
    }

    [Test]
    public void Snapshot_ListsEveryTrackedRunner_NewestBeatFirst()
    {
        var tracker = new RunnerLivenessTracker();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        tracker.Record(older, T0);
        tracker.Record(newer, T0 + TimeSpan.FromSeconds(20));

        Assert.That(tracker.Snapshot().Select(r => r.ServiceId),
            Is.EqualTo(new[] { newer, older }).AsCollection);
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
