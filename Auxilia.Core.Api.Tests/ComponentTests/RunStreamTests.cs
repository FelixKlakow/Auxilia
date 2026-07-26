using System.Net;
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
        var first = stream.MoveNextAsync();

        // The enumerator subscribes inside MoveNextAsync (after SendAsync completes). Re-publish a
        // heartbeat status until the first frame is delivered — proof the subscription is live —
        // then the later publishes are guaranteed to land.
        while (!first.IsCompleted)
        {
            await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
                new WorkflowStatusEvent(runId, DummyType, "Running", null, DateTimeOffset.UtcNow));
            await Task.Yield();
        }
        Assert.That(await first, Is.True);
        Assert.That(stream.Current.Kind, Is.EqualTo(RunStreamEvent.StatusKind));

        await MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(runId, "log", 1, """{"line":"hi"}"""));
        await MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, DummyType, "Success", null, DateTimeOffset.UtcNow));

        var rest = new List<RunStreamEvent>();
        while (await stream.MoveNextAsync())
            rest.Add(stream.Current);

        Assert.Multiple(() =>
        {
            Assert.That(rest.Any(e => e.Kind == RunStreamEvent.ViewKind && e.Sequence == 1), Is.True,
                "The view item must be delivered as a typed view frame.");
            Assert.That(rest.Any(e =>
                e.Kind == RunStreamEvent.StatusKind &&
                JsonSerializer.Deserialize<WorkflowStatusEvent>(e.PayloadJson, JsonSerializerOptions.Web)!.State == "Success"),
                Is.True, "The terminal status must be delivered and close the stream.");
        });
    }

    private static List<RunStreamEvent> ParseFrames(string body) =>
        body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.StartsWith("data: ", StringComparison.Ordinal) ? f["data: ".Length..] : f)
            .Select(f => JsonSerializer.Deserialize<RunStreamEvent>(f, JsonSerializerOptions.Web)!)
            .ToList();
}
