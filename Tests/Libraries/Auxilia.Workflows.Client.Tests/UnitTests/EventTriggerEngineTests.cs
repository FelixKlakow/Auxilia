using Auxilia.Core.Contracts;
using Auxilia.Workflows.Client.Triggers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.Workflows.Client.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public sealed class EventTriggerEngineTests
{
    private FakeCoreClient _core = null!;
    private InMemoryTriggerStore _store = null!;
    private EventTriggerEngine _engine = null!;

    [SetUp]
    public void SetUp()
    {
        _core = new FakeCoreClient();
        _store = new InMemoryTriggerStore();
        _engine = new EventTriggerEngine(_store, _core, TimeProvider.System, NullLogger<EventTriggerEngine>.Instance);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _engine.StopAsync(CancellationToken.None);
        _engine.Dispose();
    }

    private static EventStreamEvent Event(
        string type, string workItemId = "WI-1", Guid? sourceRunId = null, string? payloadJson = null)
        => new(new EventDto(Guid.NewGuid(), type, "producer-wf", workItemId, sourceRunId,
            payloadJson, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);

    [Test]
    public async Task MatchingEvent_DispatchesWithTheEventReferenceInContext()
    {
        var configId = Guid.NewGuid();
        var principal = Guid.NewGuid();
        var sourceRun = Guid.NewGuid();
        await _store.SaveAsync(new EventTriggerDefinition
        {
            EventType = "review-ready", ConfigurationId = configId,
            RunAsPrincipalId = principal,
            Context = new Dictionary<string, string> { ["chained"] = "true" }
        });

        var evt = Event("review-ready", "WI-42", sourceRun, """{"score":7}""");
        await _engine.StartAsync(CancellationToken.None);
        await _engine.HandleAsync(evt);

        var (dispatched, onBehalfOf, context) = _core.ConfigurationRuns.Single();
        Assert.Multiple(() =>
        {
            Assert.That(dispatched, Is.EqualTo(configId));
            Assert.That(onBehalfOf, Is.EqualTo(principal));
            Assert.That(context!["EventId"], Is.EqualTo(evt.Event.Id.ToString("D")));
            Assert.That(context["EventType"], Is.EqualTo("review-ready"));
            Assert.That(context["WorkItemId"], Is.EqualTo("WI-42"));
            Assert.That(context["SourceRunId"], Is.EqualTo(sourceRun.ToString("D")));
            Assert.That(context["EventPayloadJson"], Is.EqualTo("""{"score":7}"""));
            Assert.That(context["chained"], Is.EqualTo("true"),
                "the trigger's own context merges in (event keys win)");
        });
    }

    [Test]
    public async Task PayloadlessEvent_OmitsTheOptionalContextKeys()
    {
        await _store.SaveAsync(new EventTriggerDefinition
        {
            EventType = "nightly-clean", ConfigurationId = Guid.NewGuid()
        });

        await _engine.StartAsync(CancellationToken.None);
        await _engine.HandleAsync(Event("nightly-clean"));

        var (_, _, context) = _core.ConfigurationRuns.Single();
        Assert.Multiple(() =>
        {
            Assert.That(context!.ContainsKey("SourceRunId"), Is.False);
            Assert.That(context.ContainsKey("EventPayloadJson"), Is.False);
        });
    }

    [Test]
    public async Task WorkItemFilter_AndDisabled_AndOtherTypes_DoNotDispatch()
    {
        await _store.SaveAsync(new EventTriggerDefinition
        {
            EventType = "plan-done", WorkItemId = "WI-only", ConfigurationId = Guid.NewGuid()
        });
        await _store.SaveAsync(new EventTriggerDefinition
        {
            EventType = "plan-done", Enabled = false, ConfigurationId = Guid.NewGuid()
        });

        await _engine.StartAsync(CancellationToken.None);
        await _engine.HandleAsync(Event("plan-done", "WI-other"));
        await _engine.HandleAsync(Event("run.succeeded", "WI-only"));

        Assert.That(_core.ConfigurationRuns, Is.Empty);

        await _engine.HandleAsync(Event("plan-done", "WI-only"));
        Assert.That(_core.ConfigurationRuns, Has.Count.EqualTo(1),
            "only the enabled trigger with the matching work item fires");
    }

    [Test]
    public async Task Start_OpensOneServerFilteredStream_PerDistinctEventType()
    {
        await _store.SaveAsync(new EventTriggerDefinition { EventType = "plan-done", WorkflowType = "a" });
        await _store.SaveAsync(new EventTriggerDefinition { EventType = "plan-done", WorkflowType = "b" });
        await _store.SaveAsync(new EventTriggerDefinition { EventType = "run.succeeded", WorkflowType = "c" });

        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(2);

        var filters = _core.EventStreamSubscriptions.Select(s => s.EventType).ToList();
        Assert.That(filters, Is.EquivalentTo(new[] { "plan-done", "run.succeeded" }),
            "one FILTERED stream per distinct type — never an unfiltered global feed");
        Assert.That(_core.EventStreamSubscriptions.All(s => s.EventType is not null), Is.True);
    }

    [Test]
    public async Task StreamedEvent_FlowsThroughTheConsumer_ToTheDispatch()
    {
        await _store.SaveAsync(new EventTriggerDefinition
        {
            EventType = "review-ready", ConfigurationId = Guid.NewGuid()
        });
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        _core.PublishEvent(Event("review-ready"));

        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 1);
        Assert.That(_core.ConfigurationRuns, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Reconnect_CatchesUpOnEventsPublishedDuringTheGap_WithoutDoubleDispatch()
    {
        await _store.SaveAsync(new EventTriggerDefinition
        {
            EventType = "review-ready", ConfigurationId = Guid.NewGuid()
        });
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        // One live event establishes the consumer's last-seen cursor.
        var live = Event("review-ready");
        _core.PublishEvent(live);
        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 1);

        // The Core "recycles"; an event fires while this consumer is disconnected.
        var missed = new EventDto(Guid.NewGuid(), "review-ready", "producer-wf", "WI-9", null,
            null, live.Event.CreatedUtc + TimeSpan.FromSeconds(30));
        _core.StoredEvents.Add(live.Event);
        _core.StoredEvents.Add(missed);
        _core.DropAllEventStreams();

        // The resilient stream reconnects; the engine's catch-up dispatches the missed event.
        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 2);

        // The same event also arriving live (catch-up/live overlap) must NOT dispatch again.
        _core.PublishEvent(new EventStreamEvent(missed, missed.CreatedUtc));
        _core.PublishEvent(Event("review-ready", "WI-new"));
        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 3);
        Assert.That(_core.ConfigurationRuns, Has.Count.EqualTo(3),
            "the missed event dispatches exactly once (dedupe), fresh live events keep flowing");
    }

    [Test]
    public async Task DropBeforeAnyLiveEvent_StillCatchesUpFromTheConsumerStart()
    {
        await _store.SaveAsync(new EventTriggerDefinition
        {
            EventType = "review-ready", ConfigurationId = Guid.NewGuid()
        });
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        // No live event was ever handled; the Core recycles and an event fires in the gap.
        var missed = new EventDto(Guid.NewGuid(), "review-ready", "producer-wf", "WI-9", null,
            null, DateTimeOffset.UtcNow);
        _core.StoredEvents.Add(missed);
        _core.DropAllEventStreams();

        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 1);
        Assert.That(_core.ConfigurationRuns.Single().Context!["EventId"],
            Is.EqualTo(missed.Id.ToString("D")),
            "the cursor is seeded at consumer start — the gap before the first live event is not lost");
    }

    [Test]
    public async Task CatchUp_PagesUnderAFixedBound_SoASharedTimestampAtThePageEdgeIsNotSkipped()
    {
        await _store.SaveAsync(new EventTriggerDefinition
        {
            EventType = "review-ready", ConfigurationId = Guid.NewGuid()
        });
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        // 201 events share ONE timestamp: the first page (200) ends on it, the 201st sits
        // behind the page edge with the same CreatedUtc — a moving "after" bound would skip it.
        var stamp = DateTimeOffset.UtcNow;
        var gap = Enumerable.Range(0, 201)
            .Select(_ => new EventDto(Guid.NewGuid(), "review-ready", "producer-wf", "WI-page", null, null, stamp))
            .ToList();
        _core.StoredEvents.AddRange(gap);
        _core.DropAllEventStreams();

        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 201);
        var dispatchedIds = _core.ConfigurationRuns.Select(r => r.Context!["EventId"]).ToList();
        Assert.That(dispatchedIds, Is.EquivalentTo(gap.Select(e => e.Id.ToString("D"))),
            "every event of the gap dispatches exactly once");
    }

    [Test]
    public async Task DispatchTimeout_DoesNotStopTheConsumer()
    {
        await _store.SaveAsync(new EventTriggerDefinition
        {
            EventType = "review-ready", ConfigurationId = Guid.NewGuid()
        });
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        // A unary watchdog / HttpClient timeout wears an OperationCanceledException shape —
        // it is NOT the engine's own stop signal and must not end the consumer.
        _core.DispatchError = new TaskCanceledException("Core call timed out");
        _core.PublishEvent(Event("review-ready", "WI-slow"));
        await Task.Delay(100);
        _core.DispatchError = null;

        _core.PublishEvent(Event("review-ready", "WI-next"));
        await WaitUntilAsync(() => _core.ConfigurationRuns.Count == 1);
        Assert.That(_core.ConfigurationRuns.Single().Context!["WorkItemId"], Is.EqualTo("WI-next"),
            "the consumer survives the timed-out dispatch and keeps dispatching");
    }

    [Test]
    public async Task Refresh_AlignsConsumersWithTheTriggerSet()
    {
        var trigger = new EventTriggerDefinition { EventType = "plan-done", WorkflowType = "a" };
        await _store.SaveAsync(trigger);
        await _engine.StartAsync(CancellationToken.None);
        await WaitForSubscriptionsAsync(1);

        await _store.DeleteEventTriggerAsync(trigger.Id);
        await _store.SaveAsync(new EventTriggerDefinition { EventType = "review-ready", WorkflowType = "b" });
        await _engine.RefreshAsync();
        await WaitUntilAsync(() => _core.EventStreamSubscriptions.Any(s => s.EventType == "review-ready"));

        _core.PublishEvent(Event("review-ready"));
        await WaitUntilAsync(() => _core.InlineRuns.Count == 1);
        Assert.That(_core.InlineRuns.Single().WorkflowType, Is.EqualTo("b"));
    }

    private async Task WaitForSubscriptionsAsync(int count)
        => await WaitUntilAsync(() => _core.EventStreamSubscriptions.Count >= count);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.That(condition(), Is.True, "condition not reached in time");
    }
}
