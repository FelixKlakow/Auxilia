using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>Unit tests for the SSE subscriber registry: fan-out and per-run filtering.</summary>
[TestFixture]
[Category("Unit")]
public sealed class RunStreamBrokerTests
{
    private static RunStreamEvent StatusFor(Guid runId) =>
        new(RunStreamEvent.StatusKind, runId, 0, "{}", DateTimeOffset.UtcNow);

    [Test]
    public void Publish_DeliversOnlyToSubscribersOfThatRun()
    {
        var broker = new RunStreamBroker();
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();

        using var subA = broker.Subscribe(runA);
        using var subB = broker.Subscribe(runB);

        var eventA = StatusFor(runA);
        broker.Publish(eventA);

        Assert.Multiple(() =>
        {
            Assert.That(subA.Reader.TryRead(out var received), Is.True);
            Assert.That(received, Is.EqualTo(eventA));
            Assert.That(subB.Reader.TryRead(out _), Is.False, "A run-B subscriber must not see run-A events.");
        });
    }

    [Test]
    public void Publish_FansOutToEverySubscriberOfTheSameRun()
    {
        var broker = new RunStreamBroker();
        var runId = Guid.NewGuid();

        using var first = broker.Subscribe(runId);
        using var second = broker.Subscribe(runId);

        broker.Publish(StatusFor(runId));

        Assert.Multiple(() =>
        {
            Assert.That(first.Reader.TryRead(out _), Is.True);
            Assert.That(second.Reader.TryRead(out _), Is.True);
        });
    }

    [Test]
    public void Publish_WithNoSubscribers_IsANoOp()
        => Assert.DoesNotThrow(() => new RunStreamBroker().Publish(StatusFor(Guid.NewGuid())));

    [Test]
    public void DisposedSubscriber_ReceivesNoFurtherEvents()
    {
        var broker = new RunStreamBroker();
        var runId = Guid.NewGuid();
        var subscription = broker.Subscribe(runId);
        subscription.Dispose();

        broker.Publish(StatusFor(runId));

        Assert.That(subscription.Reader.TryRead(out _), Is.False);
    }
}
