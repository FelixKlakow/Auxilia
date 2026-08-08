using System.Net;
using System.Net.Http.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Component tests for the platform-event client surface: events mirrored from the
/// <c>workflow.events</c> exchange are queryable, the SSE stream is SERVER-SIDE filtered, and
/// terminal run-status transitions surface as the reserved <c>run.*</c> events — the whole loop
/// an event-trigger client rides, without ever touching the bus. (The in-memory fake bus
/// over-delivers by design; the filtering asserted here is the broker's, not routing.)
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class EventSurfaceTests : CoreApiComponentTestBase
{
    private static WorkflowEventMessage Event(
        string eventType = "review-ready", string workItemId = "WI-1",
        Guid? eventId = null, DateTimeOffset? at = null, string? payloadJson = """{"ok":true}""")
        => new(eventId ?? Guid.NewGuid(), eventType, Guid.NewGuid(), "producer-wf", workItemId,
            payloadJson, at ?? DateTimeOffset.UtcNow);

    [Test]
    public async Task Query_ReturnsMirroredEvents_FilteredByTypeAndWorkItem()
    {
        var client = CreateClient();
        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            Event(eventType: "review-ready", workItemId: "WI-1"));
        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            Event(eventType: "review-ready", workItemId: "WI-2"));
        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            Event(eventType: "plan-done", workItemId: "WI-1"));

        var all = await client.GetFromJsonAsync<PagedResult<EventDto>>("/api/events");
        Assert.That(all!.Total, Is.EqualTo(3));

        var byType = await client.GetFromJsonAsync<PagedResult<EventDto>>(
            "/api/events?eventType=review-ready");
        Assert.That(byType!.Items.Select(e => e.EventType), Is.All.EqualTo("review-ready"));
        Assert.That(byType.Total, Is.EqualTo(2));

        var byBoth = await client.GetFromJsonAsync<PagedResult<EventDto>>(
            "/api/events?eventType=review-ready&workItemId=WI-2");
        Assert.That(byBoth!.Items, Has.Count.EqualTo(1));
        Assert.That(byBoth.Items[0].WorkItemId, Is.EqualTo("WI-2"));
    }

    [Test]
    public async Task Query_CreatedAfterUtc_ReturnsOnlyNewerEvents_OldestFirst()
    {
        // The reconnect catch-up shape: everything published after the consumer's last-seen
        // timestamp, paged OLDEST-first so the gap drains deterministically.
        var client = CreateClient();
        var t0 = DateTimeOffset.UtcNow;
        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            Event(workItemId: "WI-old", at: t0 - TimeSpan.FromMinutes(10)));
        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            Event(workItemId: "WI-mid", at: t0 - TimeSpan.FromMinutes(2)));
        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            Event(workItemId: "WI-new", at: t0 - TimeSpan.FromMinutes(1)));

        var cutoff = Uri.EscapeDataString((t0 - TimeSpan.FromMinutes(5)).ToString("O"));
        var page = await client.GetFromJsonAsync<PagedResult<EventDto>>(
            $"/api/events?eventType=review-ready&createdAfterUtc={cutoff}");

        Assert.That(page!.Items.Select(e => e.WorkItemId), Is.EqualTo(new[] { "WI-mid", "WI-new" }),
            "only events after the cutoff, ordered oldest-first for deterministic paging");
    }

    [Test]
    public async Task GetById_ReturnsTheMirroredEvent_Or404()
    {
        var client = CreateClient();
        var id = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            Event(eventId: id, payloadJson: """{"score":9}"""));

        var evt = await client.GetFromJsonAsync<EventDto>($"/api/events/{id}");
        Assert.Multiple(() =>
        {
            Assert.That(evt!.Id, Is.EqualTo(id));
            Assert.That(evt.PayloadJson, Is.EqualTo("""{"score":9}"""));
        });

        var missing = await client.GetAsync($"/api/events/{Guid.NewGuid()}");
        Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task Stream_IsServerSideFiltered_ByEventType()
    {
        ICoreClient core = new CoreClient(CreateClient());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var stream = core
            .StreamEventsAsync(eventType: "review-ready", ct: cts.Token)
            .GetAsyncEnumerator(cts.Token);

        // The Connected frame arrives once the server flushed headers — i.e. AFTER the broker
        // subscription is registered — so everything published below is guaranteed delivered.
        Assert.That(await stream.MoveNextAsync(), Is.True);
        Assert.That(stream.Current,
            Is.InstanceOf<StreamConnectionFrame<EventStreamEvent>>()
                .With.Property("State").EqualTo(StreamConnectionState.Connected));

        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            Event(workItemId: "WI-live"));
        Assert.That((await NextEventAsync(stream)).Event.EventType, Is.EqualTo("review-ready"));

        // A non-matching event must be filtered SERVER-side; the next matching one arrives instead.
        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            Event(eventType: "plan-done", workItemId: "WI-noise"));
        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            Event(workItemId: "WI-match"));

        var next = await NextEventAsync(stream);
        Assert.Multiple(() =>
        {
            Assert.That(next.Event.EventType, Is.EqualTo("review-ready"),
                "the plan-done event must never reach this subscriber");
            Assert.That(next.Event.WorkItemId, Is.Not.EqualTo("WI-noise"));
        });
        cts.Cancel();
    }

    [Test]
    public async Task TerminalRunStatus_SurfacesAsAReservedRunEvent_QueryableAndTyped()
    {
        // The platform's own publisher: a terminal workflow.status transition becomes a
        // run.succeeded event on the SAME pipeline the SDK-published events ride.
        var client = CreateClient();
        var runId = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, "wf-type", "Success", null, DateTimeOffset.UtcNow));

        var page = await client.GetFromJsonAsync<PagedResult<EventDto>>(
            $"/api/events?eventType={PlatformEventTypes.RunSucceeded}");
        var evt = page!.Items.Single(e => e.SourceRunId == runId);
        Assert.Multiple(() =>
        {
            Assert.That(evt.EventType, Is.EqualTo(PlatformEventTypes.RunSucceeded));
            Assert.That(evt.WorkflowType, Is.EqualTo("wf-type"));
            Assert.That(evt.PayloadJson, Does.Contain("Success"));
        });

        // Re-delivery of the same terminal transition must not create a second event.
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, "wf-type", "Success", null, DateTimeOffset.UtcNow));
        var again = await client.GetFromJsonAsync<PagedResult<EventDto>>(
            $"/api/events?eventType={PlatformEventTypes.RunSucceeded}&sourceRunId={runId}");
        Assert.That(again!.Total, Is.EqualTo(1), "the deterministic event id collapses re-delivery");
    }

    [Test]
    public async Task Client_QueryEvents_RoundTripsTypedDtos()
    {
        ICoreClient core = new CoreClient(CreateClient());
        var id = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(WorkflowEventMessage.ExchangeName,
            Event(eventType: "plan-done", workItemId: "WI-9", eventId: id));

        var page = await core.QueryEventsAsync(new EventQuery(EventType: "plan-done"));
        Assert.That(page.Items.Single().Id, Is.EqualTo(id));
    }

    [Test]
    public async Task Surface_RequiresAuthentication()
    {
        var anonymous = CreateAnonymousClient();
        Assert.Multiple(async () =>
        {
            Assert.That((await anonymous.GetAsync("/api/events")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await anonymous.GetAsync($"/api/events/{Guid.NewGuid()}")).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await anonymous.GetAsync(
                    "/api/events/stream", HttpCompletionOption.ResponseHeadersRead)).StatusCode,
                Is.EqualTo(HttpStatusCode.Unauthorized));
        });
    }

    private static async Task<EventStreamEvent> NextEventAsync(
        IAsyncEnumerator<ClientStreamFrame<EventStreamEvent>> stream)
    {
        while (await stream.MoveNextAsync())
            if (stream.Current is StreamEventFrame<EventStreamEvent> frame)
                return frame.Event;
        throw new InvalidOperationException("the stream ended without the expected event");
    }
}
