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
/// The Runs page renders runs from the fake <see cref="ICoreClient"/>, resolves configuration names,
/// and cancels an active run via <c>CancelRunAsync</c>.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class RunsPageTests
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

    private static RunStatus Run(string type, string state, Guid? configId = null, string? configName = null)
        => new(Guid.NewGuid(), type, state, null, DateTimeOffset.UtcNow, configId, configName);

    [Test]
    public void Runs_DisplaysRuns_AndResolvesConfigurationName()
    {
        var configId = Guid.NewGuid();
        var core = new FakeCoreClient();
        core.Runs.Add(Run("code-review", "Running", configId));
        core.Configurations.Add(new RunConfiguration(
            configId, "Nightly review", "code-review",
            new Dictionary<string, string>(), [], true, DateTimeOffset.UtcNow));
        using var ctx = NewContext(core);

        var cut = ctx.Render<Runs>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("code-review"));
            Assert.That(cut.Markup, Does.Contain("Nightly review"), "the configuration name is resolved via GetConfigurationAsync");
        });
    }

    [Test]
    public void Runs_CancelActiveRun_CallsCancel()
    {
        var core = new FakeCoreClient();
        var run = Run("impl", "Running");
        core.Runs.Add(run);
        using var ctx = NewContext(core);
        var cut = ctx.Render<Runs>();

        // Cancelling a live run is destructive: it arms first, then commits.
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();
        Assert.That(core.CancelledRuns, Is.Empty, "arming the confirmation must not cancel anything yet");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel run").Click();

        Assert.That(core.CancelledRuns, Does.Contain(run.RunId), "confirming calls CancelRunAsync with the run id");
    }

    [Test]
    public void Runs_StateFilter_MapsToRunQuery()
    {
        var core = new FakeCoreClient();
        core.Runs.Add(Run("impl", "Failed"));
        using var ctx = NewContext(core);
        var cut = ctx.Render<Runs>();

        cut.FindAll(".chip-bar .chip").First(c => c.TextContent.Contains("Failed")).Click();

        Assert.That(core.LastRunQuery!.State, Is.EqualTo("Failed"), "the state chip maps to RunQuery.State");
    }

    [Test]
    public void Runs_ShowsAccessDenied_OnForbidden()
    {
        var core = new FakeCoreClient
        {
            RunsError = new CoreApiException(HttpStatusCode.Forbidden, "run.read required", "denied")
        };
        using var ctx = NewContext(core);

        var cut = ctx.Render<Runs>();

        Assert.That(cut.Markup, Does.Contain("Access denied"));
    }
}
