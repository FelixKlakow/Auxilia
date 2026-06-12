using Auxilia.BackendService.Dashboard;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.BackendService.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class LiveViewBrokerTests
{
    private static ViewDataMessage ViewData(long sequence = 1)
        => new(Guid.NewGuid(), "log", sequence, "{}");

    private static WorkflowStatusEvent Status(string state = "Running")
        => new(Guid.NewGuid(), "test-workflow", state, null, DateTimeOffset.UtcNow);

    [Test]
    public void Publish_ViewData_ReachesAllSubscribers()
    {
        var broker = new LiveViewBroker();
        var first = new List<ViewDataMessage>();
        var second = new List<ViewDataMessage>();
        using var s1 = broker.Subscribe(onViewData: first.Add);
        using var s2 = broker.Subscribe(onViewData: second.Add);
        var message = ViewData();

        broker.Publish(message);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(new[] { message }));
            Assert.That(second, Is.EqualTo(new[] { message }));
        });
    }

    [Test]
    public void Publish_StatusEvent_ReachesStatusSubscriber()
    {
        var broker = new LiveViewBroker();
        var received = new List<WorkflowStatusEvent>();
        using var subscription = broker.Subscribe(onStatus: received.Add);
        var statusEvent = Status();

        broker.Publish(statusEvent);

        Assert.That(received, Is.EqualTo(new[] { statusEvent }));
    }

    [Test]
    public void Publish_AfterDispose_DoesNotDeliver()
    {
        var broker = new LiveViewBroker();
        var received = new List<ViewDataMessage>();
        var subscription = broker.Subscribe(onViewData: received.Add);
        subscription.Dispose();

        broker.Publish(ViewData());

        Assert.That(received, Is.Empty);
    }

    [Test]
    public void Publish_FaultedSubscriber_DoesNotBreakOthers()
    {
        var broker = new LiveViewBroker();
        var received = new List<ViewDataMessage>();
        using var faulted = broker.Subscribe(onViewData: _ => throw new InvalidOperationException("dead circuit"));
        using var healthy = broker.Subscribe(onViewData: received.Add);

        Assert.DoesNotThrow(() => broker.Publish(ViewData()));
        Assert.That(received, Has.Count.EqualTo(1));
    }
}
