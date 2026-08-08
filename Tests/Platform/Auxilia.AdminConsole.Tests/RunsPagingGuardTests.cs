using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.AdminConsole.Support;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// Auto-refresh and manual paging cannot coexist: the Runs page polls every five seconds with a
/// reset load, which used to silently throw away every page the reader had loaded with "Show more".
/// Paging now pauses the poll and says so, and the reader resumes explicitly.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RunsPagingGuardTests
{
    private const int PageSize = 50;

    private static BunitContext NewContext(FakeCoreClient core)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
        ctx.Services.AddSingleton(new StepUpFlow(core));
        ctx.Services.AddSingleton(new SharingDirectory(core));
        return ctx;
    }

    private static FakeCoreClient WithRuns(int count)
    {
        var core = new FakeCoreClient();
        for (var i = 0; i < count; i++)
        {
            core.Runs.Add(new RunStatus(
                Guid.NewGuid(), $"wf-{i:D3}", "Success", null,
                DateTimeOffset.UtcNow.AddMinutes(-i), null, null));
        }
        return core;
    }

    [Test]
    public void ShowMore_AppendsThePage_AndPausesTheResetPoll()
    {
        var core = WithRuns(60);
        using var ctx = NewContext(core);
        var cut = ctx.Render<Runs>();

        Assert.That(cut.FindAll("tbody tr"), Has.Count.EqualTo(PageSize), "the first page only");

        cut.FindAll("button").First(b => b.TextContent.Contains("Show more")).Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll("tbody tr"), Has.Count.EqualTo(60), "the second page is appended");
            Assert.That(core.LastRunQuery!.Skip, Is.EqualTo(PageSize), "paging asks for the next offset");
            Assert.That(cut.Markup, Does.Contain("Auto-refresh paused"),
                "the reader is told the live refresh stepped aside rather than silently resetting");
        });
    }

    [Test]
    public void Resume_ReturnsToTheFirstPage_AndClearsTheNotice()
    {
        var core = WithRuns(60);
        using var ctx = NewContext(core);
        var cut = ctx.Render<Runs>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Show more")).Click();

        cut.FindAll("button").First(b => b.TextContent.Contains("Resume")).Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll("tbody tr"), Has.Count.EqualTo(PageSize), "resuming resets to the newest page");
            Assert.That(cut.Markup, Does.Not.Contain("Auto-refresh paused"));
        });
    }

    [Test]
    public void ChangingAFilter_ResumesTheLiveRefresh()
    {
        var core = WithRuns(60);
        using var ctx = NewContext(core);
        var cut = ctx.Render<Runs>();
        cut.FindAll("button").First(b => b.TextContent.Contains("Show more")).Click();

        cut.FindAll(".chip-bar .chip").First(c => c.TextContent.Contains("Success")).Click();

        Assert.That(cut.Markup, Does.Not.Contain("Auto-refresh paused"),
            "a filter change is a fresh browse — the poll takes over again");
    }

    [Test]
    public void StateChips_CarryTheCoreReportedCounts()
    {
        var core = WithRuns(3);
        core.Runs.Add(new RunStatus(Guid.NewGuid(), "impl", "Running", null, DateTimeOffset.UtcNow, null, null));
        using var ctx = NewContext(core);

        var cut = ctx.Render<Runs>();

        var chips = cut.FindAll(".chip-bar .chip").Select(c => c.TextContent.Trim().Replace(" ", "")).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(chips.Any(c => c.StartsWith("Running") && c.EndsWith("1")), Is.True,
                "chip counts come from the Core's own state histogram");
            Assert.That(chips.Any(c => c.StartsWith("Success") && c.EndsWith("3")), Is.True);
        });
    }
}
