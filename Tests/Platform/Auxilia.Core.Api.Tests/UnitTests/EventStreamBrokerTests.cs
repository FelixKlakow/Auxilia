using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class EventStreamBrokerTests
{
    private static EventStreamEvent Event(string eventType, string workItemId = "WI-1")
        => new(new EventDto(
                Guid.NewGuid(), eventType, "wf", workItemId, Guid.NewGuid(), null,
                DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);

    [Test]
    public async Task NullFilters_ReceiveEverything()
    {
        var broker = new EventStreamBroker();
        using var subscription = await broker.SubscribeAsync(eventType: null, workItemId: null);

        broker.Publish(Event("a"));
        broker.Publish(Event("b"));

        Assert.That(Drain(subscription), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task TypeFilter_DropsOtherTypes_ServerSide()
    {
        var broker = new EventStreamBroker();
        using var subscription = await broker.SubscribeAsync("review-ready", workItemId: null);

        broker.Publish(Event("run.succeeded"));
        broker.Publish(Event("review-ready"));
        broker.Publish(Event("run.succeeded"));

        var received = Drain(subscription);
        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received[0].Event.EventType, Is.EqualTo("review-ready"));
    }

    [Test]
    public async Task CombinedFilters_MustBothMatch()
    {
        var broker = new EventStreamBroker();
        using var subscription = await broker.SubscribeAsync("plan-done", "WI-7");

        broker.Publish(Event("plan-done", "WI-1"));
        broker.Publish(Event("other", "WI-7"));
        broker.Publish(Event("plan-done", "WI-7"));

        var received = Drain(subscription);
        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received[0].Event.WorkItemId, Is.EqualTo("WI-7"));
    }

    [Test]
    public async Task WhitespaceFilters_AreTreatedAsNoFilter()
    {
        var broker = new EventStreamBroker();
        using var subscription = await broker.SubscribeAsync("  ", "");

        broker.Publish(Event("anything", "WI-x"));

        Assert.That(Drain(subscription), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DisposedSubscription_ReceivesNothingFurther()
    {
        var broker = new EventStreamBroker();
        var subscription = await broker.SubscribeAsync(null, null);
        subscription.Dispose();

        broker.Publish(Event("a"));

        Assert.That(subscription.Reader.Completion.IsCompleted, Is.True);
        Assert.That(subscription.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public async Task Listener_SeesTheNormalizedTypeFilter()
    {
        var broker = new EventStreamBroker();
        var listener = new RecordingListener();
        broker.SetListener(listener);

        var typed = await broker.SubscribeAsync("plan-done", "WI-7");
        var unfiltered = await broker.SubscribeAsync("  ", null);
        typed.Dispose();
        unfiltered.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(listener.Added, Is.EqualTo(new[] { "plan-done", null }));
            Assert.That(listener.Removed, Is.EqualTo(new[] { "plan-done", null }));
        });
    }

    [Test]
    public void FailingListener_FailsTheSubscribe_AndLeavesNoSubscriber()
    {
        var broker = new EventStreamBroker();
        broker.SetListener(new ThrowingListener());

        Assert.ThrowsAsync<InvalidOperationException>(() => broker.SubscribeAsync("plan-done", null));
        Assert.DoesNotThrow(() => broker.Publish(Event("plan-done")));
    }

    private static List<EventStreamEvent> Drain(EventStreamBroker.Subscription subscription)
    {
        var received = new List<EventStreamEvent>();
        while (subscription.Reader.TryRead(out var evt))
            received.Add(evt);
        return received;
    }

    private sealed class RecordingListener : IEventStreamBindingListener
    {
        public List<string?> Added { get; } = new();
        public List<string?> Removed { get; } = new();

        public Task EventInterestAddedAsync(string? eventType, CancellationToken ct)
        {
            Added.Add(eventType);
            return Task.CompletedTask;
        }

        public Task EventInterestRemovedAsync(string? eventType)
        {
            Removed.Add(eventType);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingListener : IEventStreamBindingListener
    {
        public Task EventInterestAddedAsync(string? eventType, CancellationToken ct)
            => throw new InvalidOperationException("bind failed");

        public Task EventInterestRemovedAsync(string? eventType) => Task.CompletedTask;
    }
}
