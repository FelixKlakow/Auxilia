using Auxilia.Core.Contracts;
using Auxilia.Workflows.Client.Triggers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Workflows.Client.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class ArtifactChainingEngineTests
{
    private FakeCoreClient _core = null!;
    private InMemoryTriggerStore _store = null!;
    private ArtifactChainingEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _core = new FakeCoreClient();
        _store = new InMemoryTriggerStore();
        _engine = new ArtifactChainingEngine(_store, _core, TimeProvider.System, NullLogger<ArtifactChainingEngine>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _engine.StopAsync(CancellationToken.None);
        _engine.Dispose();
    }

    private static ArtifactStreamEvent Event(string type, string workItemId = "WI-1", int version = 1)
        => new(new ArtifactDto(Guid.NewGuid(), type, "producer-wf", workItemId, Guid.NewGuid(),
            version, "HASH", 5, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

    [Test]
    public async Task MatchingEvent_DispatchesWithTheArtifactReferenceInContext()
    {
        var configId = Guid.NewGuid();
        var principal = Guid.NewGuid();
        await _store.SaveAsync(new ArtifactTriggerDefinition
        {
            ArtifactType = "code-review-result", ConfigurationId = configId,
            RunAsPrincipalId = principal,
            Context = new Dictionary<string, string> { ["chained"] = "true" }
        });

        var evt = Event("code-review-result", "WI-42");
        await _engine.StartAsync(CancellationToken.None);
        await _engine.HandleAsync(evt);

        var (dispatched, onBehalfOf, context) = _core.ConfigurationRuns.Single();
        Assert.Multiple(() =>
        {
            Assert.That(dispatched, Is.EqualTo(configId));
            Assert.That(onBehalfOf, Is.EqualTo(principal));
            Assert.That(context!["ArtifactId"], Is.EqualTo(evt.Artifact.Id.ToString("D")));
            Assert.That(context["ArtifactType"], Is.EqualTo("code-review-result"));
            Assert.That(context["WorkItemId"], Is.EqualTo("WI-42"));
            Assert.That(context["chained"], Is.EqualTo("true"),
                "the trigger's own context merges in (artifact keys win)");
        });
    }

    [Test]
    public async Task WorkItemFilter_AndDisabled_AndOtherTypes_DoNotDispatch()
    {
        await _store.SaveAsync(new ArtifactTriggerDefinition
        {
            ArtifactType = "plan", WorkItemId = "WI-only", ConfigurationId = Guid.NewGuid()
        });
        await _store.SaveAsync(new ArtifactTriggerDefinition
        {
            ArtifactType = "plan", Enabled = false, ConfigurationId = Guid.NewGuid()
        });

        await _engine.StartAsync(CancellationToken.None);
        await _engine.HandleAsync(Event("plan", "WI-other"));
        await _engine.HandleAsync(Event("design-doc", "WI-only"));

        Assert.That(_core.ConfigurationRuns, Is.Empty);

        await _engine.HandleAsync(Event("plan", "WI-only"));
        Assert.That(_core.ConfigurationRuns, Has.Count.EqualTo(1),
            "only the enabled trigger with the matching work item fires");
    }

    [Test]
    public async Task Start_OpensOneServerFilteredStream_PerDistinctArtifactType()
    {
        await _store.SaveAsync(new ArtifactTriggerDefinition { ArtifactType = "plan", WorkflowType = "a" });
        await _store.SaveAsync(new ArtifactTriggerDefinition { ArtifactType = "plan", WorkflowType = "b" });
        await _store.SaveAsync(new ArtifactTriggerDefinition { ArtifactType = "review", WorkflowType = "c" });

        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(2);

        var filters = _core.StreamSubscriptions.Select(s => s.ArtifactType).ToList();
        Assert.That(filters, Is.EquivalentTo(new[] { "plan", "review" }),
            "one FILTERED stream per distinct type — never an unfiltered global feed");
        Assert.That(_core.StreamSubscriptions.All(s => s.ArtifactType is not null), Is.True);
    }

    [Test]
    public async Task StreamedEvent_FlowsThroughTheConsumer_ToTheDispatch()
    {
        await _store.SaveAsync(new ArtifactTriggerDefinition
        {
            ArtifactType = "review", ConfigurationId = Guid.NewGuid()
        });
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        _core.PublishArtifact(Event("review"));

        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 1);
        Assert.That(_core.ConfigurationRuns, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task DroppedStream_Reconnects_AndKeepsDispatching()
    {
        await _store.SaveAsync(new ArtifactTriggerDefinition
        {
            ArtifactType = "review", ConfigurationId = Guid.NewGuid()
        });
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        _core.DropAllStreams();
        await WaitForSubscriptionsAsync(1); // the reconnect opens a fresh subscription

        _core.PublishArtifact(Event("review"));
        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 1);
        Assert.That(_core.ConfigurationRuns, Has.Count.EqualTo(1),
            "after a dropped stream the engine must reconnect and keep chaining");
    }

    [Test]
    public async Task Reconnect_CatchesUpOnArtifactsPersistedDuringTheGap_WithoutDoubleDispatch()
    {
        await _store.SaveAsync(new ArtifactTriggerDefinition
        {
            ArtifactType = "review", ConfigurationId = Guid.NewGuid()
        });
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        // One live event establishes the consumer's last-seen cursor.
        var live = Event("review");
        _core.PublishArtifact(live);
        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 1);

        // The Core "recycles"; an artifact lands while this consumer is disconnected.
        var missed = new ArtifactDto(Guid.NewGuid(), "review", "producer-wf", "WI-9", Guid.NewGuid(),
            1, "HASH", 5, live.Artifact.CreatedUtc + TimeSpan.FromSeconds(30));
        _core.StoredArtifacts.Add(live.Artifact);
        _core.StoredArtifacts.Add(missed);
        _core.DropAllStreams();

        // The resilient stream reconnects; the engine's catch-up dispatches the missed artifact.
        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 2);

        // The same artifact also arriving live (catch-up/live overlap) must NOT dispatch again.
        _core.PublishArtifact(new ArtifactStreamEvent(missed, missed.CreatedUtc));
        _core.PublishArtifact(Event("review", "WI-new"));
        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 3);
        Assert.That(_core.ConfigurationRuns, Has.Count.EqualTo(3),
            "the missed artifact dispatches exactly once (dedupe), fresh live events keep flowing");
    }

    [Test]
    public async Task DropBeforeAnyLiveEvent_StillCatchesUpFromTheConsumerStart()
    {
        await _store.SaveAsync(new ArtifactTriggerDefinition
        {
            ArtifactType = "review", ConfigurationId = Guid.NewGuid()
        });
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        // No live event was ever handled; the Core recycles and an artifact lands in the gap.
        var missed = new ArtifactDto(Guid.NewGuid(), "review", "producer-wf", "WI-9", Guid.NewGuid(),
            1, "HASH", 5, DateTimeOffset.UtcNow);
        _core.StoredArtifacts.Add(missed);
        _core.DropAllStreams();

        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 1);
        Assert.That(_core.ConfigurationRuns.Single().Context!["ArtifactId"],
            Is.EqualTo(missed.Id.ToString("D")),
            "the cursor is seeded at consumer start — the gap before the first live event is not lost");
    }

    [Test]
    public async Task CatchUp_PagesUnderAFixedBound_SoASharedTimestampAtThePageEdgeIsNotSkipped()
    {
        await _store.SaveAsync(new ArtifactTriggerDefinition
        {
            ArtifactType = "review", ConfigurationId = Guid.NewGuid()
        });
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        // 201 artifacts share ONE timestamp: the first page (200) ends on it, the 201st sits
        // behind the page edge with the same CreatedUtc — a moving "after" bound would skip it.
        var stamp = DateTimeOffset.UtcNow;
        var gap = Enumerable.Range(0, 201)
            .Select(_ => new ArtifactDto(Guid.NewGuid(), "review", "producer-wf", "WI-page", Guid.NewGuid(),
                1, "HASH", 5, stamp))
            .ToList();
        _core.StoredArtifacts.AddRange(gap);
        _core.DropAllStreams();

        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 201);
        var dispatchedIds = _core.ConfigurationRuns.Select(r => r.Context!["ArtifactId"]).ToList();
        Assert.That(dispatchedIds, Is.EquivalentTo(gap.Select(a => a.Id.ToString("D"))),
            "every artifact of the gap dispatches exactly once");
    }

    [Test]
    public async Task DispatchTimeout_DoesNotStopTheConsumer()
    {
        await _store.SaveAsync(new ArtifactTriggerDefinition
        {
            ArtifactType = "review", ConfigurationId = Guid.NewGuid()
        });
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        // A unary watchdog / HttpClient timeout wears an OperationCanceledException shape —
        // it is NOT the engine's own stop signal and must not end the consumer.
        _core.DispatchError = new TaskCanceledException("Core call timed out");
        _core.PublishArtifact(Event("review", "WI-slow"));
        await Task.Delay(100);
        _core.DispatchError = null;

        _core.PublishArtifact(Event("review", "WI-next"));
        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 1);
        Assert.That(_core.ConfigurationRuns.Single().Context!["WorkItemId"], Is.EqualTo("WI-next"),
            "the consumer survives the timed-out dispatch and keeps chaining");
    }

    [Test]
    public async Task Refresh_AlignsConsumersWithTheTriggerSet()
    {
        var trigger = new ArtifactTriggerDefinition { ArtifactType = "plan", WorkflowType = "a" };
        await _store.SaveAsync(trigger);
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        await _store.DeleteArtifactTriggerAsync(trigger.Id);
        await _store.SaveAsync(new ArtifactTriggerDefinition { ArtifactType = "review", WorkflowType = "b" });
        await _engine.RefreshAsync();
        await WaitUntilAsync(() => _core.StreamSubscriptions.Any(s => s.ArtifactType == "review"));

        _core.PublishArtifact(Event("review"));
        await WaitUntilAsync(() => _core.InlineRuns.Count == 1);
        Assert.That(_core.InlineRuns.Single().WorkflowType, Is.EqualTo("b"));
    }

    private async Task WaitForSubscriptionsAsync(int count)
        => await WaitUntilAsync(() => _core.StreamSubscriptions.Count >= count);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.That(condition(), Is.True, "condition not reached in time");
    }
}
