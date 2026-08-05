using System.Text.Json;
using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The marquee run-detail page consumes the Core's SSE run stream: it decodes status frames into the
/// live run state/badge and view frames into <c>ViewItemBuffer</c>, then renders each view through
/// <c>ViewRenderer</c>. A terminal status drops the live badge. All hermetic — the fake scripts the stream.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RunDetailPageTests
{
    private static BunitContext NewContext(FakeCoreClient core)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
        ctx.Services.AddSingleton(new CoreClientOptions { BaseAddress = "https://core.test" });
        return ctx;
    }

    private static RunStreamEvent Status(Guid runId, string type, string state)
        => new(RunStreamEvent.StatusKind, runId, 0,
            JsonSerializer.Serialize(new WorkflowStatusEvent(runId, type, state, null, DateTimeOffset.UtcNow)),
            DateTimeOffset.UtcNow);

    private static RunStreamEvent View(Guid runId, string viewName, long seq, string payloadJson)
        => new(RunStreamEvent.ViewKind, runId, seq,
            JsonSerializer.Serialize(new ViewDataMessage(runId, viewName, seq, payloadJson)),
            DateTimeOffset.UtcNow);

    [Test]
    public void RunDetail_StreamsStatusAndViewItem_ThroughViewRenderer()
    {
        var runId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Runs.Add(new RunStatus(runId, "code-review", "Queued", null, DateTimeOffset.UtcNow, null, null));
        core.StreamEvents.Add(Status(runId, "code-review", "Running"));
        core.StreamEvents.Add(View(runId, "progress", 1, "{\"message\":\"hello-from-sse\"}"));
        using var ctx = NewContext(core);

        var cut = ctx.Render<RunDetail>(p => p.Add(c => c.Id, runId));

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.Markup, Does.Contain("Running"), "the status frame updates the run state");
                Assert.That(cut.Markup, Does.Contain("hello-from-sse"), "the SSE view item is decoded and rendered via ViewRenderer");
                Assert.That(cut.Markup, Does.Contain("live-badge"), "an active run shows the live badge");
            });
        });
    }

    [Test]
    public void RunDetail_BackfillsPersistedViews_AndDeduplicatesAgainstStream()
    {
        var runId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Runs.Add(new RunStatus(runId, "code-review", "Running", null, DateTimeOffset.UtcNow, null, null));
        // History published before the page was opened — only reachable via the persisted read.
        core.RunViews.Add(new RunViewItem("progress", 1, "{\"message\":\"from-the-store\"}", DateTimeOffset.UtcNow));
        // The same item also arrives over the stream (overlap) plus one genuinely new item.
        core.StreamEvents.Add(View(runId, "progress", 1, "{\"message\":\"from-the-store\"}"));
        core.StreamEvents.Add(View(runId, "progress", 2, "{\"message\":\"live-only\"}"));
        using var ctx = NewContext(core);

        var cut = ctx.Render<RunDetail>(p => p.Add(c => c.Id, runId));

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.Markup, Does.Contain("from-the-store"), "persisted history is backfilled");
                Assert.That(cut.Markup, Does.Contain("live-only"), "live items still stream in");
                var first = cut.Markup.IndexOf("from-the-store", StringComparison.Ordinal);
                Assert.That(cut.Markup.IndexOf("from-the-store", first + 1, StringComparison.Ordinal),
                    Is.EqualTo(-1), "an item present in both sources renders exactly once");
            });
        });
    }

    [Test]
    public void RunDetail_FinishedRun_RendersHistoryFromTheStore()
    {
        var runId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Runs.Add(new RunStatus(runId, "impl", "Success", null, DateTimeOffset.UtcNow, null, null));
        core.RunViews.Add(new RunViewItem("summary", 1, "{\"message\":\"replayed-history\"}", DateTimeOffset.UtcNow));
        using var ctx = NewContext(core);

        var cut = ctx.Render<RunDetail>(p => p.Add(c => c.Id, runId));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("replayed-history"),
                "a finished run replays its persisted views without any stream events"));
    }

    [Test]
    public void RunDetail_TerminalHostingRun_OpensTheTicketedProxyInANewTab()
    {
        var runId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Runs.Add(new RunStatus(
            runId, "coding-session", "Running", null, DateTimeOffset.UtcNow, null, null, HasTerminal: true));
        using var ctx = NewContext(core);

        var cut = ctx.Render<RunDetail>(p => p.Add(c => c.Id, runId));

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("button").Any(b => b.TextContent.Trim() == "Open terminal"), Is.True,
                "a live run hosting a terminal offers the button"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Open terminal").Click();

        cut.WaitForAssertion(() =>
        {
            var open = ctx.JSInterop.VerifyInvoke("open");
            Assert.That(open.Arguments[0],
                Is.EqualTo($"https://core.test/api/runs/{runId}/terminal/?ticket=fake"),
                "the ticketed proxy URL is the CORE's — the container is never reachable directly");
        });
    }

    [Test]
    public void RunDetail_WithoutTerminal_OffersNoTerminalButton()
    {
        var runId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Runs.Add(new RunStatus(runId, "impl", "Running", null, DateTimeOffset.UtcNow, null, null));
        using var ctx = NewContext(core);

        var cut = ctx.Render<RunDetail>(p => p.Add(c => c.Id, runId));

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("button").Any(b => b.TextContent.Trim() == "Open terminal"), Is.False));
    }

    [Test]
    public void RunDetail_ReconnectingFrame_ShowsTheBanner_AndKeepsRenderedData()
    {
        var runId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Runs.Add(new RunStatus(runId, "impl", "Running", null, DateTimeOffset.UtcNow, null, null));
        core.StreamFrames.Add(new StreamConnectionFrame<RunStreamEvent>(StreamConnectionState.Connected, 1));
        core.StreamFrames.Add(new StreamEventFrame<RunStreamEvent>(View(runId, "progress", 1, "{\"message\":\"still-here\"}")));
        core.StreamFrames.Add(new StreamConnectionFrame<RunStreamEvent>(
            StreamConnectionState.Reconnecting, 1, TimeSpan.FromSeconds(1), new IOException("boom")));
        using var ctx = NewContext(core);

        var cut = ctx.Render<RunDetail>(p => p.Add(c => c.Id, runId));

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.Markup, Does.Contain("connection-banner"),
                    "a reconnecting stream must be visible — never a silently frozen page");
                Assert.That(cut.Markup, Does.Contain("still-here"),
                    "already-rendered data stays visible behind the banner");
            });
        });
    }

    [Test]
    public void RunDetail_Reconnect_ClearsTheBanner_AndRefetchesRunAndViews()
    {
        var runId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Runs.Add(new RunStatus(runId, "impl", "Running", null, DateTimeOffset.UtcNow, null, null));
        core.RunViews.Add(new RunViewItem("progress", 1, "{\"message\":\"caught-up\"}", DateTimeOffset.UtcNow));
        core.StreamFrames.Add(new StreamConnectionFrame<RunStreamEvent>(StreamConnectionState.Connected, 1));
        core.StreamFrames.Add(new StreamConnectionFrame<RunStreamEvent>(
            StreamConnectionState.Reconnecting, 1, TimeSpan.Zero, new IOException("boom")));
        core.StreamFrames.Add(new StreamConnectionFrame<RunStreamEvent>(StreamConnectionState.Connected, 2));
        using var ctx = NewContext(core);

        var cut = ctx.Render<RunDetail>(p => p.Add(c => c.Id, runId));

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.Markup, Does.Not.Contain("connection-banner"),
                    "the banner clears once the stream is re-established");
                Assert.That(core.GetRunCalls, Is.GreaterThanOrEqualTo(2),
                    "a reconnect refetches the run — the state may have jumped while offline");
                Assert.That(core.GetRunViewsCalls, Is.GreaterThanOrEqualTo(2),
                    "a reconnect re-runs the persisted-view backfill");
                Assert.That(cut.Markup, Does.Contain("caught-up"));
            });
        });
    }

    [Test]
    public void RunDetail_TerminalStatus_EndsLiveBadge()
    {
        var runId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Runs.Add(new RunStatus(runId, "impl", "Running", null, DateTimeOffset.UtcNow, null, null));
        core.StreamEvents.Add(Status(runId, "impl", "Running"));
        core.StreamEvents.Add(Status(runId, "impl", "Success"));
        using var ctx = NewContext(core);

        var cut = ctx.Render<RunDetail>(p => p.Add(c => c.Id, runId));

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.Markup, Does.Contain("Success"), "the terminal status frame is applied");
                Assert.That(cut.Markup, Does.Not.Contain("live-badge"), "a terminal run drops the live badge");
            });
        });
    }
}
