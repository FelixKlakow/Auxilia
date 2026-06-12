using Auxilia.BackendService.Dashboard;
using Auxilia.PlatformData.Entities;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class ViewItemBufferTests
{
    private static ViewDataRecord Item(long sequence, string payload = "{}")
        => new()
        {
            Id = Guid.NewGuid(),
            WorkflowInstanceId = Guid.Empty,
            ViewName = "log",
            Sequence = sequence,
            PayloadJson = payload
        };

    [Test]
    public void Add_OutOfOrderItems_AreReturnedOrderedBySequence()
    {
        var buffer = new ViewItemBuffer();

        buffer.Add(Item(3));
        buffer.Add(Item(1));
        buffer.Add(Item(2));

        Assert.That(buffer.Items.Select(i => i.Sequence), Is.EqualTo(new long[] { 1, 2, 3 }));
    }

    [Test]
    public void Add_DuplicateSequence_IsRejected()
    {
        var buffer = new ViewItemBuffer();
        buffer.Add(Item(1, "{\"v\":\"first\"}"));

        var added = buffer.Add(Item(1, "{\"v\":\"duplicate\"}"));

        Assert.Multiple(() =>
        {
            Assert.That(added, Is.False);
            Assert.That(buffer.Items, Has.Count.EqualTo(1));
            Assert.That(buffer.Items[0].PayloadJson, Is.EqualTo("{\"v\":\"first\"}"));
        });
    }

    [Test]
    public void Merge_StoreReadBehindLiveItems_KeepsLiveTailWithoutRegression()
    {
        var buffer = new ViewItemBuffer();
        buffer.Add(Item(1));
        buffer.Add(Item(2));
        buffer.Add(Item(3)); // live item not yet persisted

        var changed = buffer.Merge([Item(1), Item(2)]); // store lags behind

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(buffer.Items.Select(i => i.Sequence), Is.EqualTo(new long[] { 1, 2, 3 }));
        });
    }

    [Test]
    public void Merge_StoreAheadOfLiveItems_AddsTheMissingOnes()
    {
        var buffer = new ViewItemBuffer();
        buffer.Add(Item(2));

        var changed = buffer.Merge([Item(1), Item(2), Item(3)]);

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(buffer.Items.Select(i => i.Sequence), Is.EqualTo(new long[] { 1, 2, 3 }));
        });
    }
}
