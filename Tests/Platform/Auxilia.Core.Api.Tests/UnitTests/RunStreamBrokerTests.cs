using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Unit tests for the SSE subscriber registry: fan-out, per-run filtering, and the binding
/// listener that keeps this node's bus bindings in step with its audience.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunStreamBrokerTests
{
    private static RunStreamEvent StatusFor(Guid runId) =>
        new(RunStreamEvent.StatusKind, runId, 0, "{}", DateTimeOffset.UtcNow);

    [Test]
    public async Task Publish_DeliversOnlyToSubscribersOfThatRun()
    {
        var broker = new RunStreamBroker();
        var runA = Guid.NewGuid();
        var runB = Guid.NewGuid();

        using var subA = await broker.SubscribeAsync(runA);
        using var subB = await broker.SubscribeAsync(runB);

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
    public async Task Publish_FansOutToEverySubscriberOfTheSameRun()
    {
        var broker = new RunStreamBroker();
        var runId = Guid.NewGuid();

        using var first = await broker.SubscribeAsync(runId);
        using var second = await broker.SubscribeAsync(runId);

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
    public async Task DisposedSubscriber_ReceivesNoFurtherEvents()
    {
        var broker = new RunStreamBroker();
        var runId = Guid.NewGuid();
        var subscription = await broker.SubscribeAsync(runId);
        subscription.Dispose();

        broker.Publish(StatusFor(runId));

        Assert.That(subscription.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public async Task Listener_SeesEverySubscribeAndUnsubscribe()
    {
        var broker = new RunStreamBroker();
        var listener = new RecordingListener();
        broker.SetListener(listener);
        var runId = Guid.NewGuid();

        var first = await broker.SubscribeAsync(runId);
        var second = await broker.SubscribeAsync(runId);
        first.Dispose();
        second.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(listener.Subscribed, Is.EqualTo(new[] { runId, runId }));
            Assert.That(listener.Unsubscribed, Is.EqualTo(new[] { runId, runId }));
        });
    }

    [Test]
    public void FailingListener_FailsTheSubscribe_AndLeavesNoSubscriber()
    {
        var broker = new RunStreamBroker();
        broker.SetListener(new ThrowingListener());
        var runId = Guid.NewGuid();

        Assert.ThrowsAsync<InvalidOperationException>(() => broker.SubscribeAsync(runId));

        // The half-registered channel must be gone — publishing reaches nobody and never throws.
        Assert.DoesNotThrow(() => broker.Publish(StatusFor(runId)));
    }

    private sealed class RecordingListener : IRunStreamBindingListener
    {
        public List<Guid> Subscribed { get; } = new();
        public List<Guid> Unsubscribed { get; } = new();

        public Task RunSubscribedAsync(Guid runId, CancellationToken ct)
        {
            Subscribed.Add(runId);
            return Task.CompletedTask;
        }

        public Task RunUnsubscribedAsync(Guid runId)
        {
            Unsubscribed.Add(runId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingListener : IRunStreamBindingListener
    {
        public Task RunSubscribedAsync(Guid runId, CancellationToken ct)
            => throw new InvalidOperationException("bind failed");

        public Task RunUnsubscribedAsync(Guid runId) => Task.CompletedTask;
    }
}
