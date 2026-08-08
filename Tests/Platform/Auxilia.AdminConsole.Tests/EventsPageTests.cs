using System.Net;
using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The Events page renders against a fake <see cref="ICoreClient"/>: it queries
/// <c>QueryEventsAsync</c>, displays events newest-first with a platform chip on the reserved
/// <c>run.*</c> vocabulary, maps the filter inputs onto <see cref="EventQuery"/>, links the
/// source run, and surfaces a Core authorization failure as an access-denied panel.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class EventsPageTests
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

    private static EventDto Event(
        string type, string workItemId = "WI-1", Guid? sourceRunId = null,
        string? payloadJson = null, DateTimeOffset? at = null)
        => new(Guid.NewGuid(), type, "producer-wf", workItemId, sourceRunId,
            payloadJson, at ?? DateTimeOffset.UtcNow);

    [Test]
    public void Events_DisplaysEventsFromCore_WithPlatformChipOnReservedTypes()
    {
        var runId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.StoredEvents.Add(Event("review-ready", "WI-42", runId, """{"score":9}"""));
        core.StoredEvents.Add(Event(PlatformEventTypes.RunSucceeded, "", runId,
            at: DateTimeOffset.UtcNow.AddMinutes(-1)));
        using var ctx = NewContext(core);

        var cut = ctx.Render<Events>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("review-ready"));
            Assert.That(cut.Markup, Does.Contain("run.succeeded"));
            Assert.That(cut.Markup, Does.Contain(">platform<"),
                "the reserved run.* vocabulary carries the platform chip");
            Assert.That(cut.Markup, Does.Contain($"runs/{runId}"), "the source run links to RunDetail");
        });
    }

    [Test]
    public void Events_AppliesTypeFilter_ToTheQuery()
    {
        var core = new FakeCoreClient();
        core.StoredEvents.Add(Event("review-ready"));
        core.StoredEvents.Add(Event("plan-done", "WI-other"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<Events>();

        cut.Find("input[placeholder='e.g. review-ready']").Input("plan-done");
        cut.FindAll(".events-toolbar button").First(b => b.TextContent.Contains("Apply")).Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.LastEventQuery!.EventType, Is.EqualTo("plan-done"),
                "the filter maps to EventQuery.EventType");
            Assert.That(cut.Markup, Does.Contain("WI-other"), "the matching event stays");
            Assert.That(cut.Markup, Does.Not.Contain("review-ready\"") | Does.Not.Contain(">review-ready<"),
                "the non-matching event is filtered out");
        });
    }

    [Test]
    public void Events_ExpandsARow_ToThePrettyPayload()
    {
        var core = new FakeCoreClient();
        core.StoredEvents.Add(Event("review-ready", payloadJson: """{"score":9}"""));
        using var ctx = NewContext(core);
        var cut = ctx.Render<Events>();

        cut.Find("tr.row-expandable .row-toggle").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("row-detail"), "the row expands");
            Assert.That(cut.Markup, Does.Contain("Event id"));
        });
    }

    /// <summary>
    /// The time filter is an upper bound only: CreatedAfterUtc flips the Core into its oldest-first
    /// catch-up shape, which is a replay contract, not a browse filter.
    /// </summary>
    [Test]
    public void Events_BeforeDate_MapsToTheBrowseShapedUpperBound()
    {
        var core = new FakeCoreClient();
        core.StoredEvents.Add(Event("review-ready"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<Events>();

        cut.Find("input[type=date]").Change("2026-08-01");
        cut.Find("form.events-toolbar").Submit();

        Assert.Multiple(() =>
        {
            Assert.That(core.LastEventQuery!.CreatedBeforeUtc, Is.Not.Null);
            Assert.That(core.LastEventQuery.CreatedBeforeUtc!.Value.Date, Is.EqualTo(new DateTime(2026, 8, 2)),
                "the whole of the chosen day is included");
            Assert.That(core.LastEventQuery.CreatedAfterUtc, Is.Null,
                "the catch-up parameter must never leak into a browse query");
        });
    }

    [Test]
    public void Events_BeforeDate_DisablesFollowLive()
    {
        var core = new FakeCoreClient();
        core.StoredEvents.Add(Event("review-ready"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<Events>();

        cut.Find("input[type=date]").Change("2026-08-01");
        cut.Find("form.events-toolbar").Submit();

        var follow = cut.FindAll("button").First(b => b.TextContent.Contains("Follow live"));
        Assert.That(follow.HasAttribute("disabled"), Is.True,
            "a historical window and a live tail contradict each other");
    }

    [Test]
    public void Events_ShowsAccessDenied_OnForbidden()
    {
        var core = new FakeCoreClient
        {
            EventsError = new CoreApiException(
                HttpStatusCode.Forbidden, "event.consume required", "Core API request failed")
        };
        using var ctx = NewContext(core);

        var cut = ctx.Render<Events>();

        Assert.That(cut.Markup, Does.Contain("Access denied"), "a 403 renders the denied panel");
    }

    [Test]
    public void Events_EmptyStore_RendersTheEmptyState()
    {
        using var ctx = NewContext(new FakeCoreClient());

        var cut = ctx.Render<Events>();

        Assert.That(cut.Markup, Does.Contain("No events yet"));
    }
}
