using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// Filter state lives in the URL, so a filtered view is shareable, bookmarkable, and survives a
/// refresh — and so one page can deep-link into another's filter (the run detail's "View all →"
/// points at <c>/events?sourceRun={id}</c>).
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class PageFiltersTests
{
    private static BunitContext NewContext(FakeCoreClient core, string? url = null)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
        ctx.Services.AddSingleton(new StepUpFlow(core));
        ctx.Services.AddSingleton(new SharingDirectory(core));
        if (url is not null)
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return ctx;
    }

    private static EventDto Event(string type, Guid? runId)
        => new(Guid.NewGuid(), type, "producer-wf", "WI-1", runId, """{"score":9}""", DateTimeOffset.UtcNow);

    [Test]
    public void Events_SourceRunInTheUrl_PreFiltersTheQuery()
    {
        var runId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.StoredEvents.Add(Event("review-ready", runId));
        core.StoredEvents.Add(Event("other-thing", Guid.NewGuid()));
        using var ctx = NewContext(core, $"http://localhost/events?sourceRun={runId}");

        var cut = ctx.Render<Events>();

        Assert.Multiple(() =>
        {
            Assert.That(core.LastEventQuery!.SourceRunId, Is.EqualTo(runId),
                "the run detail's 'View all' link lands pre-filtered");
            Assert.That(cut.Markup, Does.Contain("review-ready"));
            Assert.That(cut.Markup, Does.Not.Contain("other-thing"));
        });
    }

    [Test]
    public void Events_ApplyingAFilter_PushesItIntoTheUrl()
    {
        var core = new FakeCoreClient();
        core.StoredEvents.Add(Event("review-ready", Guid.NewGuid()));
        using var ctx = NewContext(core);
        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        var cut = ctx.Render<Events>();

        cut.Find("input[placeholder='e.g. review-ready']").Input("plan-done");
        cut.Find("form.events-toolbar").Submit();

        Assert.That(navigation.Uri, Does.Contain("eventType=plan-done"),
            "the filtered view is a link someone else can open");
    }

    [Test]
    public void Events_ClearFilters_EmptiesBothTheQueryAndTheUrl()
    {
        var core = new FakeCoreClient();
        core.StoredEvents.Add(Event("review-ready", Guid.NewGuid()));
        using var ctx = NewContext(core, "http://localhost/events?eventType=nothing-matches");
        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        var cut = ctx.Render<Events>();

        Assert.That(cut.Markup, Does.Contain("No events match these filters"),
            "an empty result caused by a filter says so, instead of claiming there is nothing");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Clear filters").Click();

        Assert.Multiple(() =>
        {
            Assert.That(core.LastEventQuery!.EventType, Is.Null);
            Assert.That(navigation.Uri, Does.Not.Contain("eventType"));
            Assert.That(cut.Markup, Does.Contain("review-ready"));
        });
    }

    [Test]
    public void Audit_FiltersRoundTripThroughTheUrl()
    {
        var core = new FakeCoreClient();
        core.AuditEntries.Add(new AuditEntry(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "svc", "workflow.rerun", "run/1", "Allowed", null));
        using var ctx = NewContext(core, "http://localhost/audit?action=workflow.rerun");

        ctx.Render<Audit>();

        Assert.That(core.LastAuditQuery!.Action, Is.EqualTo("workflow.rerun"),
            "an audit link carries its whole filter set");
    }

    [Test]
    public void Runs_StateFilterRoundTripsThroughTheUrl()
    {
        var core = new FakeCoreClient();
        core.Runs.Add(new RunStatus(Guid.NewGuid(), "impl", "Failed", null, DateTimeOffset.UtcNow, null, null));
        using var ctx = NewContext(core, "http://localhost/runs?state=Failed");

        ctx.Render<Runs>();

        Assert.That(core.LastRunQuery!.State, Is.EqualTo("Failed"));
    }
}
