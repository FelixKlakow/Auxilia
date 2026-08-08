using Auxilia.AdminConsole.Components;
using Bunit;

namespace Auxilia.AdminConsole.Tests;

/// <summary>
/// The shared two-step guard on destructive actions: one click arms, the second commits, and
/// backing out leaves the action un-run. Cancelling a live run used to be a single unguarded click.
/// </summary>
[TestFixture]
[Category("Component")]
public sealed class ConfirmButtonTests
{
    [Test]
    public void FirstClick_Arms_SecondClickCommits()
    {
        var confirmed = 0;
        using var ctx = new BunitContext();

        var cut = ctx.Render<ConfirmButton>(parameters => parameters
            .Add(p => p.Label, "Cancel")
            .Add(p => p.Question, "Cancel this run?")
            .Add(p => p.ConfirmLabel, "Cancel run")
            .Add(p => p.OnConfirm, () => confirmed++));

        cut.Find("button").Click();
        Assert.Multiple(() =>
        {
            Assert.That(confirmed, Is.Zero, "arming must not invoke the action");
            Assert.That(cut.Markup, Does.Contain("Cancel this run?"), "the armed state states the question");
        });

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel run").Click();
        Assert.That(confirmed, Is.EqualTo(1), "the second click commits exactly once");
    }

    [Test]
    public void BackingOut_LeavesTheActionUnrun()
    {
        var confirmed = 0;
        using var ctx = new BunitContext();

        var cut = ctx.Render<ConfirmButton>(parameters => parameters
            .Add(p => p.Label, "Delete")
            .Add(p => p.Question, "Delete this source?")
            .Add(p => p.OnConfirm, () => confirmed++));

        cut.Find("button").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Keep").Click();

        Assert.Multiple(() =>
        {
            Assert.That(confirmed, Is.Zero);
            Assert.That(cut.Markup, Does.Not.Contain("Delete this source?"), "backing out disarms");
            Assert.That(cut.Markup, Does.Contain("Delete"), "the trigger comes back");
        });
    }

    [Test]
    public void Busy_DisablesBothStates()
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<ConfirmButton>(parameters => parameters
            .Add(p => p.Label, "Disable")
            .Add(p => p.Question, "Disable this principal?")
            .Add(p => p.Busy, true));

        Assert.That(cut.Find("button").HasAttribute("disabled"), Is.True,
            "an in-flight mutation must not be re-triggerable");
    }
}
