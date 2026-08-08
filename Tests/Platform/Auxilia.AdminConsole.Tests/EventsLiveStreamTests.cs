using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// Following live must be honest and non-disruptive: a reconnecting stream raises the connection
/// banner (it used to drop connection frames and keep looking live), and arrivals are buffered
/// behind an explicit "N new events" instead of being inserted under the reader's eyes.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class EventsLiveStreamTests
{
    private static BunitContext NewContext(FakeCoreClient core)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
        ctx.Services.AddSingleton(new StepUpFlow(core));
        ctx.Services.AddSingleton(new SharingDirectory(core));
        return ctx;
    }

    private static EventDto Event(string type)
        => new(Guid.NewGuid(), type, "producer-wf", "WI-1", Guid.NewGuid(),
            """{"score":9}""", DateTimeOffset.UtcNow);

    private static ClientStreamFrame<EventStreamEvent> Frame(EventDto dto)
        => new StreamEventFrame<EventStreamEvent>(new EventStreamEvent(dto, dto.CreatedUtc));

    [Test]
    public void FollowLive_BuffersArrivals_UntilTheReaderAsksForThem()
    {
        var core = new FakeCoreClient();
        core.StoredEvents.Add(Event("already-here"));
        core.EventStreamFrames.Add(Frame(Event("arrived-live")));
        using var ctx = NewContext(core);
        var cut = ctx.Render<Events>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Follow live")).Click();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("1 new event"),
            "a live arrival is announced, not silently prepended"));
        Assert.That(cut.Markup, Does.Not.Contain("arrived-live"),
            "the table does not shift while the reader is reading it");

        cut.FindAll("button").First(b => b.TextContent.Contains("new event")).Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("arrived-live"), "flushing prepends the buffered events");
            Assert.That(cut.Markup, Does.Contain("row-fresh"), "and flags them so the change is visible");
        });
    }

    [Test]
    public void FollowLive_ReconnectingStream_RaisesTheConnectionBanner()
    {
        var core = new FakeCoreClient();
        core.StoredEvents.Add(Event("already-here"));
        core.EventStreamFrames.Add(new StreamConnectionFrame<EventStreamEvent>(
            StreamConnectionState.Reconnecting, 2, TimeSpan.FromSeconds(1), new IOException("core restarted")));
        using var ctx = NewContext(core);
        var cut = ctx.Render<Events>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Follow live")).Click();

        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Connection to the Core lost"),
            "a silently reconnecting stream must never keep looking live"));
    }

    [Test]
    public void FollowLive_PassesTheCurrentFilters_ToTheServerSideSubscription()
    {
        var core = new FakeCoreClient();
        core.StoredEvents.Add(Event("review-ready"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<Events>();

        cut.Find("input[placeholder='e.g. review-ready']").Input("review-ready");
        cut.Find("form.events-toolbar").Submit();
        cut.FindAll("button").First(b => b.TextContent.Contains("Follow live")).Click();

        cut.WaitForAssertion(() => Assert.That(core.LastEventStreamFilter?.EventType, Is.EqualTo("review-ready"),
            "the stream is filtered server-side under the same filter as the table"));
    }

    [Test]
    public void PayloadColumn_ShowsSummaryFields_NotTruncatedJson()
    {
        var core = new FakeCoreClient();
        core.StoredEvents.Add(new EventDto(
            Guid.NewGuid(), "review-ready", "producer-wf", "WI-1", Guid.NewGuid(),
            """{"pullRequest":128,"approved":true}""", DateTimeOffset.UtcNow));
        using var ctx = NewContext(core);

        var cut = ctx.Render<Events>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("pullRequest"), "the payload's own fields are readable");
            Assert.That(cut.Markup, Does.Contain("128"));
            Assert.That(cut.FindAll(".field-summary"), Is.Not.Empty);
        });
    }
}
