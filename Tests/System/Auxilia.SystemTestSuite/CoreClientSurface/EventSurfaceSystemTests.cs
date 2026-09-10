using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Client.Triggers;
using Auxilia.Workflows.Events;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auxilia.SystemTestSuite.CoreClientSurface;

/// <summary>
/// The platform-event surface against the real Dockerized Core: events published through the
/// REAL SDK publisher over REAL RabbitMQ topic routing reach the Core's mirror, its filtered
/// SSE stream, and an <see cref="EventTriggerEngine"/> that dispatches a follow-up run which
/// actually executes in a container — the full event-trigger loop, plus the platform's own
/// <c>run.succeeded</c> lifecycle event over the same pipeline. Complements the component
/// tests (in-proc, fake bus that over-delivers by design) with the routing truth.
/// </summary>
[TestFixture]
[Category("System")]
public sealed class EventSurfaceSystemTests
{
    private static ICoreClient Admin => _admin ??= CoreClientEnvironment.CreateClient();
    private static ICoreClient? _admin;

    /// <summary>The real SDK publisher over the environment's real bus — the container code path.</summary>
    private static DefaultEventPublisher NewPublisher(params string[] declaredTypes)
        => new(CoreClientEnvironment.MessageBusClient, Guid.NewGuid(), "client-surface-producer",
            declaredTypes.Select(t => new EventDescriptor(t)).ToList());

    [Test]
    [CancelAfter(120_000)]
    public async Task Events_TopicFilteredStream_Query_And_Catchup(CancellationToken ct)
    {
        const string wanted = "client-surface-signal";
        const string unwanted = "client-surface-noise";
        var publisher = NewPublisher(wanted, unwanted);

        // Subscribe FILTERED first (real topic routing — the fake bus cannot verify this),
        // then publish events of two types via the real SDK publisher.
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var received = new List<EventStreamEvent>();
        var streamTask = Task.Run(async () =>
        {
            await foreach (var frame in Admin.StreamEventsAsync(wanted, ct: streamCts.Token))
                if (frame is StreamEventFrame<EventStreamEvent> evt)
                    received.Add(evt.Event);
        }, CancellationToken.None);
        await Task.Delay(2000, ct); // let the SSE subscription and its bus binding settle

        var t0 = DateTimeOffset.UtcNow;
        await publisher.PublishAsync(wanted, new { step = "first" }, workItemId: "WI-1", ct: ct);
        await publisher.PublishAsync(unwanted, workItemId: "WI-noise", ct: ct);
        await publisher.PublishAsync(wanted, new { step = "second" }, workItemId: "WI-2", ct: ct);

        // The filtered stream must deliver both wanted events and NEVER the noise type.
        await WaitUntilAsync(() => received.Count >= 2, TimeSpan.FromSeconds(20), ct);
        streamCts.Cancel();
        try { await streamTask; } catch (OperationCanceledException) { }
        Assert.Multiple(() =>
        {
            Assert.That(received.Select(e => e.Event.EventType), Is.All.EqualTo(wanted));
            Assert.That(received.Select(e => e.Event.WorkItemId), Is.EquivalentTo(new[] { "WI-1", "WI-2" }));
            Assert.That(received.Select(e => e.Event.PayloadJson), Has.Some.Contains("first"));
        });

        // Query: newest-first by default, oldest-first in the createdAfterUtc catch-up shape.
        var byType = await Admin.QueryEventsAsync(new EventQuery(EventType: wanted), ct);
        Assert.That(byType.Items.Select(e => e.WorkItemId), Is.SupersetOf(new[] { "WI-1", "WI-2" }),
            "the tracking mirror must have persisted both events");

        var catchup = await Admin.QueryEventsAsync(
            new EventQuery(EventType: wanted, CreatedAfterUtc: t0.AddSeconds(-10)), ct);
        Assert.That(catchup.Items.Select(e => e.CreatedUtc), Is.Ordered.Ascending,
            "catch-up pages oldest-first so a gap drains deterministically");
    }

    [Test]
    [CancelAfter(180_000)]
    public async Task EventTrigger_EndToEnd_PublishedEventDispatchesTheFollowUpRun(CancellationToken ct)
    {
        const string chainEvent = "client-surface-chain";
        var marker = $"chained-{Guid.NewGuid():N}";

        // A real trigger engine over the real client: trigger on the event type, dispatch the
        // echo workflow inline with the trigger's own context (the dummy image selects by
        // WORKFLOW_NAME; MESSAGE proves THIS trigger produced the run).
        var store = new InMemoryTriggerStore();
        await store.SaveAsync(new EventTriggerDefinition
        {
            EventType = chainEvent,
            WorkflowType = CoreClientEnvironment.EchoWorkflowType,
            Context = CoreClientEnvironment.ContextFor(
                CoreClientEnvironment.EchoWorkflowType, ("MESSAGE", marker))
        }, ct);
        var engine = new EventTriggerEngine(store, Admin, TimeProvider.System, NullLogger<EventTriggerEngine>.Instance);
        await engine.StartAsync(ct);
        try
        {
            await Task.Delay(2000, ct); // let the engine's filtered SSE stream and binding settle

            var before = (await Admin.QueryRunsAsync(
                new RunQuery(WorkflowType: CoreClientEnvironment.EchoWorkflowType, Take: 200), ct))
                .Items.Select(r => r.RunId).ToHashSet();

            await NewPublisher(chainEvent).PublishAsync(chainEvent, workItemId: "WI-chain", ct: ct);

            // The engine consumes the filtered stream and dispatches through the Run API — a new
            // echo run appears and runs to Success in a real container.
            Guid? followUp = null;
            await WaitUntilAsync(async () =>
            {
                var page = await Admin.QueryRunsAsync(
                    new RunQuery(WorkflowType: CoreClientEnvironment.EchoWorkflowType, Take: 200), ct);
                followUp = page.Items.Select(r => r.RunId).FirstOrDefault(id => !before.Contains(id));
                return followUp is { } id && id != Guid.Empty;
            }, TimeSpan.FromSeconds(60), ct);
            Assert.That(followUp, Is.Not.Null.And.Not.EqualTo(Guid.Empty),
                "the published event must have dispatched a follow-up run");

            var (state, _) = await ClientStreamProbe.AwaitTerminalAsync(Admin, followUp!.Value, ct);
            Assert.That(state, Is.EqualTo("Success"));

            var views = await Admin.GetRunViewsAsync(followUp.Value, ct: ct);
            Assert.That(views.Items.Select(v => v.PayloadJson), Has.Some.Contains(marker),
                "the follow-up run must carry the trigger's context — proving THIS trigger dispatched it");

            // The same terminal transition rides the pipeline back as the platform's OWN event:
            // the run-lifecycle publisher turns Success into run.succeeded (deterministic id —
            // exactly ONE event no matter how status re-delivery behaves).
            await WaitUntilAsync(async () =>
            {
                var events = await Admin.QueryEventsAsync(new EventQuery(
                    EventType: PlatformEventTypes.RunSucceeded, SourceRunId: followUp), ct);
                return events.Total == 1;
            }, TimeSpan.FromSeconds(30), ct);
            var lifecycle = await Admin.QueryEventsAsync(new EventQuery(
                EventType: PlatformEventTypes.RunSucceeded, SourceRunId: followUp), ct);
            Assert.Multiple(() =>
            {
                Assert.That(lifecycle.Total, Is.EqualTo(1),
                    "exactly one run.succeeded event per run — the deterministic id collapses duplicates");
                Assert.That(lifecycle.Items.Single().WorkflowType,
                    Is.EqualTo(CoreClientEnvironment.EchoWorkflowType));
            });
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None);
            engine.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                return; // let the caller's assertion report the actual state
            await Task.Delay(250, ct);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
                return; // let the caller's assertion report the actual state
            await Task.Delay(500, ct);
        }
    }
}
