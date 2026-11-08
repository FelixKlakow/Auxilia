using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.AspNetCore.Hosting;

namespace Auxilia.Core.Api.Tests.ComponentTests;

/// <summary>
/// The resumable run stream: a client connecting late, or coming back with the position it got
/// last (<c>Last-Event-ID</c> / <c>?after=</c>), gets every event exactly once, in order — stored
/// items first, then the live ones; the run's verdict last.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RunStreamResumeTests : CoreApiComponentTestBase
{
    private const string DummyType = "simple-git-commit-workflow";

    protected override void ConfigureHost(IWebHostBuilder builder)
        => builder.UseSetting("CoreApi:StreamGapWaitMilliseconds", "400");

    [Test]
    public async Task ALateClient_GetsTheCurrentState_EveryStoredItemInOrder_ThenTheLiveOnes()
    {
        var runId = await RunningAsync();
        for (var seq = 1; seq <= 5; seq++)
            await ViewAsync(runId, seq, seq % 2 == 0 ? "steering" : "log");

        using var response = await OpenAsync(runId);
        using var sse = new SseFrames(await response.Content.ReadAsStreamAsync());

        Assert.That(SseFrames.StateOf(await sse.NextAsync()), Is.EqualTo("Running"), "where the run stands, first");
        for (var seq = 1; seq <= 5; seq++)
            Assert.That((await sse.NextAsync())!.Sequence, Is.EqualTo(seq), "the stored items, in publish order");

        await ViewAsync(runId, 6);
        Assert.That((await sse.NextAsync())!.Sequence, Is.EqualTo(6), "then the live ones");
        await StatusAsync(runId, "Success", lastViewSequence: 6);
        Assert.That(SseFrames.StateOf(await sse.NextAsync()), Is.EqualTo("Success"));
        Assert.That(await sse.NextAsync(), Is.Null, "the verdict closes the stream");
    }

    [Test]
    public async Task AReconnect_WithLastEventId_ContinuesRightAfterIt_ExactlyOnce()
    {
        var runId = await RunningAsync();
        for (var seq = 1; seq <= 3; seq++)
            await ViewAsync(runId, seq);

        var seen = new List<RunStreamEvent>();
        string lastId;
        using (var first = await OpenAsync(runId))
        using (var sse = new SseFrames(await first.Content.ReadAsStreamAsync()))
        {
            for (var i = 0; i < 4; i++)
                seen.Add((await sse.NextAsync())!);
            lastId = sse.LastId!;
        }

        // Published while the client is away.
        await ViewAsync(runId, 4);
        await ViewAsync(runId, 5);

        using var second = await OpenAsync(runId, lastEventId: lastId);
        using var resumed = new SseFrames(await second.Content.ReadAsStreamAsync());
        seen.Add((await resumed.NextAsync())!);
        seen.Add((await resumed.NextAsync())!);
        await ViewAsync(runId, 6);
        seen.Add((await resumed.NextAsync())!);
        await StatusAsync(runId, "Success", lastViewSequence: 6);
        seen.Add((await resumed.NextAsync())!);
        Assert.That(await resumed.NextAsync(), Is.Null);

        Assert.Multiple(() =>
        {
            Assert.That(seen.Where(f => f.Kind == RunStreamEvent.ViewKind).Select(f => f.Sequence),
                Is.EqualTo(new long[] { 1, 2, 3, 4, 5, 6 }), "every item once, in order, across both connections");
            Assert.That(seen.Select(SseFrames.StateOf).Where(s => s is not null),
                Is.EqualTo(new[] { "Running", "Success" }), "an unchanged status is not sent again on the resume");
            Assert.That(seen.All(f => f.Cursor is { Length: > 0 }), Is.True, "every frame carries its position");
        });
    }

    [Test]
    public async Task AResume_AfterTheStatusChanged_GetsTheNewStateOnce_First()
    {
        var runId = Guid.NewGuid();
        await SeedRunAsync(runId);

        string lastId;
        using (var first = await OpenAsync(runId))
        using (var sse = new SseFrames(await first.Content.ReadAsStreamAsync()))
        {
            Assert.That(SseFrames.StateOf(await sse.NextAsync()), Is.EqualTo(RunStates.Dispatched));
            lastId = sse.LastId!;
        }

        await StatusAsync(runId, "Running");
        await ViewAsync(runId, 1);
        await ViewAsync(runId, 2);

        using var second = await OpenAsync(runId, lastEventId: lastId);
        using var resumed = new SseFrames(await second.Content.ReadAsStreamAsync());
        Assert.That(SseFrames.StateOf(await resumed.NextAsync()), Is.EqualTo("Running"));
        Assert.That((await resumed.NextAsync())!.Sequence, Is.EqualTo(1));
        Assert.That((await resumed.NextAsync())!.Sequence, Is.EqualTo(2));
    }

    [Test]
    public async Task AfterParameter_StartsFromAKeptPosition_AndTheHeaderWinsOverIt()
    {
        var runId = await RunningAsync();
        for (var seq = 1; seq <= 4; seq++)
            await ViewAsync(runId, seq);

        string afterTwo;
        using (var first = await OpenAsync(runId))
        using (var sse = new SseFrames(await first.Content.ReadAsStreamAsync()))
        {
            for (var i = 0; i < 3; i++)
                await sse.NextAsync(); // status, 1, 2
            afterTwo = sse.LastId!;
        }

        using (var byQuery = await OpenAsync(runId, after: afterTwo))
        using (var sse = new SseFrames(await byQuery.Content.ReadAsStreamAsync()))
            Assert.That((await sse.NextAsync())!.Sequence, Is.EqualTo(3), "?after= resumes a first connect");

        // A browser's EventSource keeps its URL (with the old ?after=) and sends the newer
        // Last-Event-ID on its own reconnects.
        using var both = await OpenAsync(runId, lastEventId: afterTwo, after: "0");
        using var resumed = new SseFrames(await both.Content.ReadAsStreamAsync());
        Assert.That((await resumed.NextAsync())!.Sequence, Is.EqualTo(3));
    }

    [Test]
    public async Task AFinishedRun_StreamsItsItemsThenTheVerdict_AndAResumeAtTheEndIs204()
    {
        var runId = await RunningAsync();
        await ViewAsync(runId, 1);
        await ViewAsync(runId, 2);
        await StatusAsync(runId, "Success", reason: "completed", lastViewSequence: 2);

        using var response = await OpenAsync(runId);
        var frames = RunStreamTests.ParseFrames(await response.Content.ReadAsStringAsync());
        Assert.Multiple(() =>
        {
            Assert.That(frames.Select(f => f.Kind), Is.EqualTo(new[]
            {
                RunStreamEvent.ViewKind, RunStreamEvent.ViewKind, RunStreamEvent.StatusKind,
            }), "a finished run: its items, then its verdict — nothing else");
            Assert.That(frames[^1].AsStatus()!.Reason, Is.EqualTo("completed"), "the verdict says how the run ended");
        });

        using var again = await OpenAsync(runId, lastEventId: frames[^1].Cursor);
        Assert.That(again.StatusCode, Is.EqualTo(HttpStatusCode.NoContent),
            "nothing more, ever — 204 also stops a browser's EventSource from reconnecting");
    }

    [Test]
    public async Task ItemsArrivingOutOfOrder_AreDeliveredInOrder()
    {
        var runId = await RunningAsync();
        using var response = await OpenAsync(runId);
        using var sse = new SseFrames(await response.Content.ReadAsStreamAsync());
        await sse.NextAsync(); // status

        await ViewAsync(runId, 2);
        await ViewAsync(runId, 1);

        Assert.That((await sse.NextAsync())!.Sequence, Is.EqualTo(1));
        Assert.That((await sse.NextAsync())!.Sequence, Is.EqualTo(2));
    }

    [Test]
    public async Task ItemsTheCoreNeverReceived_AreReportedAsAGap_AndTheStreamGoesOn()
    {
        var runId = await RunningAsync();
        using var response = await OpenAsync(runId);
        using var sse = new SseFrames(await response.Content.ReadAsStreamAsync());
        await sse.NextAsync(); // status

        await ViewAsync(runId, 1);
        await ViewAsync(runId, 4); // 2 and 3 never arrive

        Assert.That((await sse.NextAsync())!.Sequence, Is.EqualTo(1));
        var gap = (await sse.NextAsync())!;
        Assert.Multiple(() =>
        {
            Assert.That(gap.Kind, Is.EqualTo(RunStreamEvent.GapKind));
            Assert.That(gap.AsGap(), Is.EqualTo(new RunStreamGap(2, 3)), "exactly the missing range");
        });
        Assert.That((await sse.NextAsync())!.Sequence, Is.EqualTo(4));
    }

    [Test]
    public async Task TheVerdict_WaitsForEveryItemTheRunReported_StillInFlight()
    {
        var runId = await RunningAsync();
        using var response = await OpenAsync(runId);
        using var sse = new SseFrames(await response.Content.ReadAsStreamAsync());
        await sse.NextAsync(); // status
        await ViewAsync(runId, 1);
        Assert.That((await sse.NextAsync())!.Sequence, Is.EqualTo(1));

        // The verdict overtakes the last two items on the bus.
        await StatusAsync(runId, "Success", lastViewSequence: 3);
        await ViewAsync(runId, 2);
        await ViewAsync(runId, 3);

        var rest = new List<RunStreamEvent>();
        while (await sse.NextAsync() is { } frame)
            rest.Add(frame);
        Assert.Multiple(() =>
        {
            Assert.That(rest.Where(f => f.Kind == RunStreamEvent.ViewKind).Select(f => f.Sequence),
                Is.EqualTo(new long[] { 2, 3 }));
            Assert.That(SseFrames.StateOf(rest[^1]), Is.EqualTo("Success"), "the verdict last");
        });
    }

    [Test]
    public async Task TheVerdict_ReportsTheItemsThatNeverCame_AsAGap()
    {
        var runId = await RunningAsync();
        using var response = await OpenAsync(runId);
        using var sse = new SseFrames(await response.Content.ReadAsStreamAsync());
        await sse.NextAsync(); // status
        await ViewAsync(runId, 1);
        Assert.That((await sse.NextAsync())!.Sequence, Is.EqualTo(1));

        await StatusAsync(runId, "Success", lastViewSequence: 2);

        var gap = (await sse.NextAsync())!;
        Assert.That(gap.AsGap(), Is.EqualTo(new RunStreamGap(2, 2)), "the run said it published two");
        Assert.That(SseFrames.StateOf(await sse.NextAsync()), Is.EqualTo("Success"));
    }

    [Test]
    public async Task StoredViews_After_ReturnsTheItemsAfterThatSequence_InPublishOrder()
    {
        var client = CreateClient();
        var runId = await RunningAsync();
        await ViewAsync(runId, 1, "log");
        await ViewAsync(runId, 2, "steering");
        await ViewAsync(runId, 3, "log");

        var all = await client.GetFromJsonAsync<PagedResult<RunViewItem>>($"/api/runs/{runId}/views");
        var after = await client.GetFromJsonAsync<PagedResult<RunViewItem>>($"/api/runs/{runId}/views?after=1");

        Assert.Multiple(() =>
        {
            Assert.That(all!.Items.Select(i => (i.ViewName, i.Sequence)),
                Is.EqualTo(new[] { ("log", 1L), ("steering", 2L), ("log", 3L) }));
            Assert.That(after!.Items.Select(i => i.Sequence), Is.EqualTo(new long[] { 2, 3 }));
        });
    }

    [Test]
    public async Task APositionThisCoreNeverIssued_Is400()
    {
        var runId = await RunningAsync();
        using var response = await OpenAsync(runId, lastEventId: "not-a-position");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task TheFinalStatus_CarriesTheWorkflowsReason()
    {
        var runId = await RunningAsync();
        await StatusAsync(runId, "Success", reason: "idle-timeout");

        var status = await CreateClient().GetFromJsonAsync<RunStatus>($"/api/runs/{runId}");
        Assert.That(status!.Reason, Is.EqualTo("idle-timeout"));
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Guid> RunningAsync()
    {
        var runId = Guid.NewGuid();
        await SeedRunAsync(runId);
        await StatusAsync(runId, "Running");
        return runId;
    }

    private Task ViewAsync(Guid runId, long sequence, string view = "log")
        => MessageBus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(runId, view, sequence, JsonSerializer.Serialize(new { item = sequence })));

    private Task StatusAsync(Guid runId, string state, string? reason = null, long? lastViewSequence = null)
        => MessageBus.SimulateReceivedAsync(WorkflowStatusEvent.ExchangeName,
            new WorkflowStatusEvent(runId, DummyType, state, null, DateTimeOffset.UtcNow,
                Reason: reason, LastViewSequence: lastViewSequence));

    private async Task<HttpResponseMessage> OpenAsync(Guid runId, string? lastEventId = null, string? after = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/runs/{runId}/stream" + (after is null ? "" : $"?after={Uri.EscapeDataString(after)}"));
        if (lastEventId is not null)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        return await CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }
}
