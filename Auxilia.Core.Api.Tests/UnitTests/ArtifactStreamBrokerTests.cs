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
    public void NullFilters_ReceiveEverything()
    {
        var broker = new ArtifactStreamBroker();
        using var subscription = broker.Subscribe(artifactType: null, workItemId: null);

        broker.Publish(Event("a"));
        broker.Publish(Event("b"));

        Assert.That(Drain(subscription), Has.Count.EqualTo(2));
    }

    [Test]
    public void TypeFilter_DropsOtherTypes_ServerSide()
    {
        var broker = new ArtifactStreamBroker();
        using var subscription = broker.Subscribe("code-review-result", workItemId: null);

        broker.Publish(Event("design-doc"));
        broker.Publish(Event("code-review-result"));
        broker.Publish(Event("design-doc"));

        var received = Drain(subscription);
        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received[0].Artifact.ArtifactType, Is.EqualTo("code-review-result"));
    }

    [Test]
    public void CombinedFilters_MustBothMatch()
    {
        var broker = new ArtifactStreamBroker();
        using var subscription = broker.Subscribe("plan", "WI-7");

        broker.Publish(Event("plan", "WI-1"));
        broker.Publish(Event("other", "WI-7"));
        broker.Publish(Event("plan", "WI-7"));

        var received = Drain(subscription);
        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received[0].Artifact.WorkItemId, Is.EqualTo("WI-7"));
    }

    [Test]
    public void WhitespaceFilters_AreTreatedAsNoFilter()
    {
        var broker = new ArtifactStreamBroker();
        using var subscription = broker.Subscribe("  ", "");

        broker.Publish(Event("anything", "WI-x"));

        Assert.That(Drain(subscription), Has.Count.EqualTo(1));
    }

    [Test]
    public void DisposedSubscription_ReceivesNothingFurther()
    {
        var broker = new ArtifactStreamBroker();
        var subscription = broker.Subscribe(null, null);
        subscription.Dispose();

        broker.Publish(Event("a"));

        Assert.That(subscription.Reader.Completion.IsCompleted, Is.True);
        Assert.That(subscription.Reader.TryRead(out _), Is.False);
    }

    private static List<ArtifactStreamEvent> Drain(ArtifactStreamBroker.Subscription subscription)
    {
        var received = new List<ArtifactStreamEvent>();
        while (subscription.Reader.TryRead(out var evt))
            received.Add(evt);
        return received;
    }
}
