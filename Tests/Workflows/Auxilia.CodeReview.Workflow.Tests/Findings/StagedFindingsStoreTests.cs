using Auxilia.CodeReview.Workflow.Findings;

namespace Auxilia.CodeReview.Workflow.Tests.Findings;

[TestFixture]
public sealed class StagedFindingsStoreTests
{
    private static StagedFinding MakeFinding(string path = "a.cs") =>
        new(path, 1, 2, FindingSeverity.Info, "Style", "msg", null, "primary-reviewer");

    [Test]
    public void Append_SingleFinding_SnapshotContainsIt()
    {
        var store = new StagedFindingsStore();
        store.Append(MakeFinding());
        Assert.That(store.Snapshot(), Has.Count.EqualTo(1));
    }

    [Test]
    public void Snapshot_IsPointInTime_NotAffectedByLaterAppends()
    {
        var store = new StagedFindingsStore();
        store.Append(MakeFinding("a.cs"));
        var snap1 = store.Snapshot();
        store.Append(MakeFinding("b.cs"));
        // snap1 was taken before second append
        Assert.That(snap1.Count, Is.EqualTo(1));
        Assert.That(store.Snapshot().Count, Is.EqualTo(2));
    }

    [Test]
    public void ApproximateSizeBytes_PositiveAfterAppend()
    {
        var store = new StagedFindingsStore();
        store.Append(MakeFinding());
        Assert.That(store.ApproximateSizeBytes, Is.GreaterThan(0));
    }

    [Test]
    public void ApproximateSizeBytes_ZeroWhenEmpty()
    {
        var store = new StagedFindingsStore();
        Assert.That(store.ApproximateSizeBytes, Is.EqualTo(0));
    }

    [Test]
    public void Append_MultipleFindings_AllInSnapshot()
    {
        var store = new StagedFindingsStore();
        for (int i = 0; i < 5; i++)
            store.Append(MakeFinding($"file{i}.cs"));

        Assert.That(store.Snapshot(), Has.Count.EqualTo(5));
    }
}
