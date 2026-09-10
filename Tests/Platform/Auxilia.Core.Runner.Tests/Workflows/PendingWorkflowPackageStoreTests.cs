using Auxilia.Core.Runner.Workflows.Storage;

namespace Auxilia.Core.Runner.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class PendingWorkflowPackageStoreTests
{
    [Test]
    public void TwoConcurrentRunsOfOneType_KeepTheirOwnPackages()
    {
        var store = new PendingWorkflowPackageStore();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        store.Store(first, "/tmp/auxilia-wf-first");
        store.Store(second, "/tmp/auxilia-wf-second");

        Assert.Multiple(() =>
        {
            Assert.That(store.TryConsume(first, out var firstPath), Is.True);
            Assert.That(firstPath, Is.EqualTo("/tmp/auxilia-wf-first"));
            Assert.That(store.TryConsume(second, out var secondPath), Is.True);
            Assert.That(secondPath, Is.EqualTo("/tmp/auxilia-wf-second"));
        });
    }

    [Test]
    public void TryConsume_RemovesTheEntry()
    {
        var store = new PendingWorkflowPackageStore();
        var instanceId = Guid.NewGuid();
        store.Store(instanceId, "/tmp/auxilia-wf-x");

        Assert.Multiple(() =>
        {
            Assert.That(store.TryConsume(instanceId, out _), Is.True);
            Assert.That(store.TryConsume(instanceId, out _), Is.False, "each entry is consumed exactly once");
        });
    }
}
