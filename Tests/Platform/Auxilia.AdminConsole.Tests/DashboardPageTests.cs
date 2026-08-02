using Auxilia.AdminConsole.Components.Pages;
using Auxilia.AdminConsole.Rendering;
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The dashboard splits a single recent-window <c>QueryRunsAsync</c> into a live-now list and recent
/// runs, each linking to the run-detail page, with client-side counts. Hermetic — no network.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class DashboardPageTests
{
    private static BunitContext NewContext(FakeCoreClient core)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ICoreClient>(core);
        ctx.Services.AddSingleton(new ViewRendererRegistry([]));
        return ctx;
    }

    [Test]
    public void Dashboard_RendersRunningAndRecent()
    {
        var running = new RunStatus(Guid.NewGuid(), "impl", "Running", null, DateTimeOffset.UtcNow, null, null);
        var done = new RunStatus(Guid.NewGuid(), "code-review", "Success", null, DateTimeOffset.UtcNow.AddMinutes(-5), null, null);
        var core = new FakeCoreClient();
        core.Runs.Add(running);
        core.Runs.Add(done);
        using var ctx = NewContext(core);

        var cut = ctx.Render<Dashboard>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Live now"), "the live-now section renders");
            Assert.That(cut.Markup, Does.Contain("impl"), "the active run appears");
            Assert.That(cut.Markup, Does.Contain("code-review"), "the recent run appears");
            Assert.That(cut.Markup, Does.Contain($"runs/{running.RunId}"), "runs link to the run-detail page");
        });
    }
}
