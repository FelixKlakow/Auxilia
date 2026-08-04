using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// Component tests for the SSE live-view stream (<c>GET /api/runs/{id}/stream</c>): bus status +
/// view messages are fanned to the caller as discriminated <see cref="RunStreamEvent"/>s, the stream
/// closes on a terminal status, and the typed <see cref="ICoreClient.StreamRunAsync"/> parses it.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RunStreamTests : CoreApiComponentTestBase
{
    private const string DummyType = "simple-git-commit-workflow";

    [Test]
    public async Task Stream_FansStatusAndViewEvents_AndClosesOnTerminal()
    {
        var client = CreateClient();
        var runId = Guid.NewGuid();

        // ResponseHeadersRead returns after the endpoint flushed its headers — i.e. after the
        // broker subscription is registered — so no event published below is missed.
        using var response = await client.GetAsync(
            $"/api/runs/{runId}/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("text/event-stream"));

        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, DummyType, "Running", null, DateTimeOffset.UtcNow));
        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(runId, "log", 7, """{"line":"hello"}"""));
        // A terminal transition ends the stream, which lets ReadAsStringAsync complete.
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, DummyType, "Success", null, DateTimeOffset.UtcNow));

        var frames = ParseFrames(await response.Content.ReadAsStringAsync());

        Assert.That(frames, Has.Count.EqualTo(3));

        var running = frames[0];
        Assert.Multiple(() =>
        {
            Assert.That(running.Kind, Is.EqualTo(RunStreamEvent.StatusKind));
            Assert.That(running.RunId, Is.EqualTo(runId));
            Assert.That(JsonSerializer.Deserialize<WorkflowStatusEvent>(running.PayloadJson, JsonSerializerOptions.Web)!.State,
                Is.EqualTo("Running"));
        });

        var view = frames[1];
        Assert.Multiple(() =>
        {
            Assert.That(view.Kind, Is.EqualTo(RunStreamEvent.ViewKind));
            Assert.That(view.RunId, Is.EqualTo(runId));
            Assert.That(view.Sequence, Is.EqualTo(7));
            var item = JsonSerializer.Deserialize<ViewDataMessage>(view.PayloadJson, JsonSerializerOptions.Web)!;
            Assert.That(item.ViewName, Is.EqualTo("log"));
            Assert.That(item.PayloadJson, Does.Contain("hello"));
        });

        Assert.That(frames[2].Kind, Is.EqualTo(RunStreamEvent.StatusKind));
    }

    [Test]
    public async Task Stream_DeliversAFailure_ToTheWaitingClient_AndCloses()
    {
        // The container-crash path end to end from a client's perspective: a subscriber waiting on
        // the run's stream receives the Failed status WITH the failure reason, and the stream ends
        // (terminal) instead of leaving the client hanging.
        var client = CreateClient();
        var runId = Guid.NewGuid();

        using var response = await client.GetAsync(
            $"/api/runs/{runId}/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, DummyType, "Queued", null, DateTimeOffset.UtcNow));
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, DummyType, "Failed",
                "workflow container exited (code 139) before completing — last output: boom",
                DateTimeOffset.UtcNow));

        // ReadAsStringAsync completes only because the Failed status terminated the stream.
        var frames = ParseFrames(await response.Content.ReadAsStringAsync());

        Assert.That(frames, Has.Count.EqualTo(2));
        var failed = JsonSerializer.Deserialize<WorkflowStatusEvent>(
            frames[1].PayloadJson, JsonSerializerOptions.Web)!;
        Assert.Multiple(() =>
        {
            Assert.That(failed.State, Is.EqualTo("Failed"));
            Assert.That(failed.ErrorMessage, Does.Contain("exited (code 139)").And.Contain("boom"),
                "the client must see WHY the run failed, not just that it did");
        });
    }

    [Test]
    public async Task PersistedViews_AreReadableAfterTheRunFinished()
    {
        // The read-later counterpart of the stream: view items are mirrored into the Core store
        // and queryable after the fact — no live subscription required.
        var client = CreateClient();
        var runId = Guid.NewGuid();

        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(runId, "output", 1, """{"line":"first"}"""));
        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(runId, "output", 2, """{"line":"second"}"""));
        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(Guid.NewGuid(), "output", 1, """{"line":"other run"}"""));

        var page = await client.GetFromJsonAsync<PagedResult<RunViewItem>>($"/api/runs/{runId}/views");

        Assert.That(page!.Items, Has.Count.EqualTo(2), "only this run's items");
        Assert.Multiple(() =>
        {
            Assert.That(page.Items[0].Sequence, Is.EqualTo(1));
            Assert.That(page.Items[0].PayloadJson, Does.Contain("first"));
            Assert.That(page.Items[1].PayloadJson, Does.Contain("second"));
            Assert.That(page.Items.All(i => i.ViewName == "output"), Is.True);
        });
    }

    [Test]
    public async Task ProvideInput_DeliversTheOpaquePayload_ToTheInstanceInputQueue()
    {
        // The steer-back half of the loop: an authorized caller posts an opaque payload by the
        // DISPATCH id; the Core resolves the instance and publishes to its dedicated INPUT queue
        // verbatim (its own queue — a standing subscriber must never compete with the response
        // queue's slot-activation/configuration messages).
        var client = CreateClient();
        var commandId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, DummyType, "Running", null, DateTimeOffset.UtcNow,
                OwnerServiceId: Guid.NewGuid(), CommandId: commandId));

        var response = await client.PostAsJsonAsync($"/api/runs/{commandId}/inputs",
            new ProvideRunInput("""{"$type":"action-decision","actionId":"a1","outcome":0}"""));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var delivered = MessageBus.PublishedMessages
            .Where(m => m.Topic == $"workflow-response-{instanceId}-inputs")
            .Select(m => m.Message).OfType<WorkflowInputMessage>().Single();
        Assert.Multiple(() =>
        {
            Assert.That(delivered.WorkflowInstanceId, Is.EqualTo(instanceId));
            Assert.That(delivered.PayloadJson, Does.Contain("action-decision"),
                "the payload rides verbatim — the Core never interprets it");
        });
    }

    [Test]
    public async Task ProvideInput_IntoAFinishedRun_IsRejected()
    {
        var client = CreateClient();
        var instanceId = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, DummyType, "Success", null, DateTimeOffset.UtcNow));

        var response = await client.PostAsJsonAsync($"/api/runs/{instanceId}/inputs",
            new ProvideRunInput("{}"));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task ProvideInput_UnknownRun_Is404()
    {
        var response = await CreateClient().PostAsJsonAsync($"/api/runs/{Guid.NewGuid()}/inputs",
            new ProvideRunInput("{}"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task ClearRuns_DeletesFinishedRunsAndTheirViews_KeepsLiveOnes()
    {
        var client = CreateClient();
        var finished = Guid.NewGuid();
        var live = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(finished, DummyType, "Success", null, DateTimeOffset.UtcNow));
        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(finished, "output", 1, "{}"));
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(live, DummyType, "Running", null, DateTimeOffset.UtcNow));

        var clear = await client.DeleteAsync("/api/runs");
        Assert.That(clear.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var runs = await client.GetFromJsonAsync<PagedResult<RunStatus>>("/api/runs");
        Assert.That(runs!.Items.Select(r => r.RunId), Is.EquivalentTo(new[] { live }),
            "finished runs go, live ones stay");
        var views = await client.GetFromJsonAsync<PagedResult<RunViewItem>>($"/api/runs/{finished}/views");
        Assert.That(views!.Items, Is.Empty, "the cleared run's persisted views go with it");
    }

    [Test]
    public async Task ClearRuns_IncludeStale_RemovesZombies_KeepsFreshLiveRuns()
    {
        // A zombie: still "Running" but its terminal event was missed (Core downtime) — it has
        // not updated for ages. A genuinely live run keeps updating and must survive.
        var client = CreateClient();
        var zombie = Guid.NewGuid();
        var live = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(zombie, DummyType, "Running", null, DateTimeOffset.UtcNow.AddHours(-2)));
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(live, DummyType, "Running", null, DateTimeOffset.UtcNow));

        var clear = await client.DeleteAsync("/api/runs?includeStale=true&staleMinutes=30");
        Assert.That(clear.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var runs = await client.GetFromJsonAsync<PagedResult<RunStatus>>("/api/runs");
        Assert.That(runs!.Items.Select(r => r.RunId), Is.EquivalentTo(new[] { live }),
            "the zombie goes, the live run stays");
    }

    [Test]
    public async Task Stream_ByDispatchCommandId_ReceivesTheRunnersEvents()
    {
        // A dispatch is acknowledged with its CommandId; the runner emits events under its own
        // instance id and stamps the CommandId on the claim transition. Observing by the dispatch
        // id must therefore work — this is exactly what a steering client that just called run does.
        var client = CreateClient();
        var commandId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();

        using var response = await client.GetAsync(
            $"/api/runs/{commandId}/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, DummyType, "Received", null, DateTimeOffset.UtcNow,
                OwnerServiceId: Guid.NewGuid(), CommandId: commandId));
        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(instanceId, "log", 1, """{"line":"from the instance"}"""));
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(instanceId, DummyType, "Success", null, DateTimeOffset.UtcNow));

        var frames = ParseFrames(await response.Content.ReadAsStringAsync());

        Assert.That(frames, Has.Count.EqualTo(3),
            "claim status + view + terminal status must all reach the dispatch-id subscriber");
        Assert.Multiple(() =>
        {
            Assert.That(frames.All(f => f.RunId == commandId), Is.True);
            Assert.That(frames[0].Kind, Is.EqualTo(RunStreamEvent.StatusKind));
            Assert.That(frames[1].Kind, Is.EqualTo(RunStreamEvent.ViewKind));
            Assert.That(frames[1].PayloadJson, Does.Contain("from the instance"));
            Assert.That(frames[2].Kind, Is.EqualTo(RunStreamEvent.StatusKind));
        });
    }

    [Test]
    public async Task Stream_OnlyDeliversEventsForItsOwnRun()
    {
        var client = CreateClient();
        var runId = Guid.NewGuid();
        var otherRun = Guid.NewGuid();

        using var response = await client.GetAsync(
            $"/api/runs/{runId}/stream", HttpCompletionOption.ResponseHeadersRead);

        // An event for a different run must not appear on this stream.
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(otherRun, DummyType, "Running", null, DateTimeOffset.UtcNow));
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, DummyType, "Success", null, DateTimeOffset.UtcNow));

        var frames = ParseFrames(await response.Content.ReadAsStringAsync());
        Assert.That(frames, Has.Count.EqualTo(1));
        Assert.That(frames[0].RunId, Is.EqualTo(runId));
    }

    [Test]
    public async Task Stream_RequiresAuthentication()
    {
        var response = await CreateAnonymousClient().GetAsync(
            $"/api/runs/{Guid.NewGuid()}/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Client_StreamRunAsync_YieldsTypedEvents()
    {
        ICoreClient core = new CoreClient(CreateClient());
        var runId = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var stream = core.StreamRunAsync(runId, cts.Token).GetAsyncEnumerator(cts.Token);

        // The Connected frame arrives once the server flushed headers — the broker subscription
        // is registered, so every publish below is guaranteed delivered.
        Assert.That(await stream.MoveNextAsync(), Is.True);
        Assert.That(stream.Current,
            Is.InstanceOf<StreamConnectionFrame<RunStreamEvent>>()
                .With.Property("State").EqualTo(StreamConnectionState.Connected));

        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(runId, "log", 1, """{"line":"hi"}"""));
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, DummyType, "Success", null, DateTimeOffset.UtcNow));

        var rest = new List<RunStreamEvent>();
        while (await stream.MoveNextAsync())
            if (stream.Current is StreamEventFrame<RunStreamEvent> frame)
                rest.Add(frame.Event);

        Assert.Multiple(() =>
        {
            Assert.That(rest.Any(e => e.Kind == RunStreamEvent.ViewKind && e.Sequence == 1), Is.True,
                "The view item must be delivered as a typed view frame.");
            Assert.That(rest.Any(e =>
                e.Kind == RunStreamEvent.StatusKind &&
                JsonSerializer.Deserialize<WorkflowStatusEvent>(e.PayloadJson, JsonSerializerOptions.Web)!.State == "Success"),
                Is.True, "The terminal status must be delivered and close the stream (no reconnect).");
        });
    }

    [Test]
    public async Task Stream_SnapshotFrame_DeliversCurrentState_ToALateSubscriber()
    {
        // The reconnect-after-restart scenario: the run advanced while nobody was subscribed.
        // A fresh subscriber must learn the current state from the snapshot frame instead of
        // waiting (forever) for the next live transition.
        var client = CreateClient();
        var runId = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, DummyType, "Running", null, DateTimeOffset.UtcNow));

        using var response = await client.GetAsync(
            $"/api/runs/{runId}/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);
        var firstData = await ReadNextDataLineAsync(reader).WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = JsonSerializer.Deserialize<RunStreamEvent>(firstData!, JsonSerializerOptions.Web)!;
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Kind, Is.EqualTo(RunStreamEvent.StatusKind));
            Assert.That(JsonSerializer.Deserialize<WorkflowStatusEvent>(
                snapshot.PayloadJson, JsonSerializerOptions.Web)!.State, Is.EqualTo("Running"));
        });
    }

    [Test]
    public async Task Stream_TerminalRecord_SnapshotThenImmediateClose()
    {
        var client = CreateClient();
        var runId = Guid.NewGuid();
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, DummyType, "Success", null, DateTimeOffset.UtcNow));

        using var response = await client.GetAsync(
            $"/api/runs/{runId}/stream", HttpCompletionOption.ResponseHeadersRead);
        var frames = ParseFrames(await response.Content.ReadAsStringAsync());

        Assert.That(frames, Has.Count.EqualTo(1),
            "a finished run yields exactly its terminal snapshot and the stream closes");
        Assert.That(JsonSerializer.Deserialize<WorkflowStatusEvent>(
            frames[0].PayloadJson, JsonSerializerOptions.Web)!.State, Is.EqualTo("Success"));
    }

    [Test]
    public async Task Run_IsVisibleAsDispatched_ImmediatelyAfterAccept()
    {
        // The bug this wave fixes: "dispatched" used to be a client-side notice with no record
        // behind it. Now the accept itself creates the run — visible in the API and as an SSE
        // snapshot — before any runner claims it.
        var client = CreateClient();
        await RegisterActiveTypeAsync(client, DummyType);

        var accept = await client.PostAsJsonAsync("/api/runs",
            new RunRequest(DummyType, new Dictionary<string, string>()));
        Assert.That(accept.IsSuccessStatusCode, Is.True, await accept.Content.ReadAsStringAsync());
        var accepted = (await accept.Content.ReadFromJsonAsync<RunAccepted>())!;

        var status = await client.GetFromJsonAsync<RunStatus>($"/api/runs/{accepted.RunId}");
        Assert.That(status!.State, Is.EqualTo(RunStates.Dispatched));

        using var response = await client.GetAsync(
            $"/api/runs/{accepted.RunId}/stream", HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);
        var firstData = await ReadNextDataLineAsync(reader).WaitAsync(TimeSpan.FromSeconds(10));
        var snapshot = JsonSerializer.Deserialize<RunStreamEvent>(firstData!, JsonSerializerOptions.Web)!;
        Assert.That(JsonSerializer.Deserialize<WorkflowStatusEvent>(
            snapshot.PayloadJson, JsonSerializerOptions.Web)!.State, Is.EqualTo(RunStates.Dispatched),
            "a subscriber by the accepted run id sees the Dispatched snapshot immediately");
    }

    private static async Task<string?> ReadNextDataLineAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                return line["data: ".Length..];
        return null;
    }

    private static List<RunStreamEvent> ParseFrames(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(f => !f.StartsWith(':'))
            .Select(f => f.StartsWith("data: ", StringComparison.Ordinal) ? f["data: ".Length..] : f)
            .Select(f => JsonSerializer.Deserialize<RunStreamEvent>(f, JsonSerializerOptions.Web)!)
            .ToList();
}
