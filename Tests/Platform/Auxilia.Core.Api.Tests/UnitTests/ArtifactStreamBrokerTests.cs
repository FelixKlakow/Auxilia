using Auxilia.Core.Api.Services;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class ArtifactStreamBrokerTests
{
    private static ArtifactStreamEvent Event(string artifactType, string workItemId = "WI-1")
        => new(new ArtifactDto(
                Guid.NewGuid(), artifactType, "wf", workItemId, Guid.NewGuid(), 1, "HASH", 10,
                DateTimeOffset.UtcNow),
            DateTimeOffset.UtcNow);

    [Test]
    public async Task NullFilters_ReceiveEverything()
    {
        var broker = new ArtifactStreamBroker();
        using var subscription = await broker.SubscribeAsync(artifactType: null, workItemId: null);

        broker.Publish(Event("a"));
        broker.Publish(Event("b"));

        Assert.That(Drain(subscription), Has.Count.EqualTo(2));
    }

    [Test]
    public async Task TypeFilter_DropsOtherTypes_ServerSide()
    {
        var broker = new ArtifactStreamBroker();
        using var subscription = await broker.SubscribeAsync("code-review-result", workItemId: null);

        broker.Publish(Event("design-doc"));
        broker.Publish(Event("code-review-result"));
        broker.Publish(Event("design-doc"));

        var received = Drain(subscription);
        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received[0].Artifact.ArtifactType, Is.EqualTo("code-review-result"));
    }

    [Test]
    public async Task CombinedFilters_MustBothMatch()
    {
        var broker = new ArtifactStreamBroker();
        using var subscription = await broker.SubscribeAsync("plan", "WI-7");

        broker.Publish(Event("plan", "WI-1"));
        broker.Publish(Event("other", "WI-7"));
        broker.Publish(Event("plan", "WI-7"));

        var received = Drain(subscription);
        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received[0].Artifact.WorkItemId, Is.EqualTo("WI-7"));
    }

    [Test]
    public async Task WhitespaceFilters_AreTreatedAsNoFilter()
    {
        var broker = new ArtifactStreamBroker();
        using var subscription = await broker.SubscribeAsync("  ", "");

        broker.Publish(Event("anything", "WI-x"));

        Assert.That(Drain(subscription), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DisposedSubscription_ReceivesNothingFurther()
    {
        var broker = new ArtifactStreamBroker();
        var subscription = await broker.SubscribeAsync(null, null);
        subscription.Dispose();

        broker.Publish(Event("a"));

        Assert.That(subscription.Reader.Completion.IsCompleted, Is.True);
        Assert.That(subscription.Reader.TryRead(out _), Is.False);
    }

    [Test]
    public async Task Listener_SeesTheNormalizedTypeFilter()
    {
        var broker = new ArtifactStreamBroker();
        var listener = new RecordingListener();
        broker.SetListener(listener);

        var typed = await broker.SubscribeAsync("plan", "WI-7");
        var unfiltered = await broker.SubscribeAsync("  ", null);
        typed.Dispose();
        unfiltered.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(listener.Added, Is.EqualTo(new[] { "plan", null }));
            Assert.That(listener.Removed, Is.EqualTo(new[] { "plan", null }));
        });
    }

    [Test]
    public void FailingListener_FailsTheSubscribe_AndLeavesNoSubscriber()
    {
        var broker = new ArtifactStreamBroker();
        broker.SetListener(new ThrowingListener());

        Assert.ThrowsAsync<InvalidOperationException>(() => broker.SubscribeAsync("plan", null));
        Assert.DoesNotThrow(() => broker.Publish(Event("plan")));
    }

    private static List<ArtifactStreamEvent> Drain(ArtifactStreamBroker.Subscription subscription)
    {
        var received = new List<ArtifactStreamEvent>();
        while (subscription.Reader.TryRead(out var evt))
            received.Add(evt);
        return received;
    }

    private sealed class RecordingListener : IArtifactStreamBindingListener
    {
        public List<string?> Added { get; } = new();
        public List<string?> Removed { get; } = new();

        public Task ArtifactInterestAddedAsync(string? artifactType, CancellationToken ct)
        {
            Added.Add(artifactType);
            return Task.CompletedTask;
        }

        public Task ArtifactInterestRemovedAsync(string? artifactType)
        {
            Removed.Add(artifactType);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingListener : IArtifactStreamBindingListener
    {
        public Task ArtifactInterestAddedAsync(string? artifactType, CancellationToken ct)
            => throw new InvalidOperationException("bind failed");

        public Task ArtifactInterestRemovedAsync(string? artifactType) => Task.CompletedTask;
    }
}
