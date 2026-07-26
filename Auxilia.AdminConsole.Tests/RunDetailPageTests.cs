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
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
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
