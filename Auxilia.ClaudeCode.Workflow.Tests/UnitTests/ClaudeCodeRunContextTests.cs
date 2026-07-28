using Auxilia.Workflows.AiAgent.CodingAgent;

namespace Auxilia.ClaudeCode.Workflow.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class AgentSessionContextTests
{
    [Test]
    public void TitleAndBody_AreJoinedIntoTheInstruction()
    {
        var context = AgentSessionContext.FromValues(
            "Fix the login bug", "Users report 500s on /login.", "/workspace", "/output");

        Assert.Multiple(() =>
        {
            Assert.That(context.Instruction,
                Is.EqualTo("Fix the login bug\n\nUsers report 500s on /login."));
            Assert.That(context.WorkspaceDirectory, Is.EqualTo("/workspace"));
            Assert.That(context.OutputDirectory, Is.EqualTo("/output"));
        });
    }

    [Test]
    public void TitleOnly_IsTheWholeInstruction()
    {
        var context = AgentSessionContext.FromValues("Fix the login bug", null, "/w", "/o");

        Assert.That(context.Instruction, Is.EqualTo("Fix the login bug"));
    }

    [TestCase(null, null)]
    [TestCase("", "  ")]
    public void MissingInstruction_FailsLoudly(string? title, string? body)
    {
        Assert.Throws<InvalidOperationException>(
            () => AgentSessionContext.FromValues(title, body, "/w", "/o"));
    }

    [TestCase("true")]
    [TestCase("True")]
    [TestCase("1")]
    public void MultiTurn_MissingInstruction_IsAllowed(string multiTurn)
    {
        var context = AgentSessionContext.FromValues(
            null, null, "/w", "/o", multiTurn: multiTurn);

        Assert.Multiple(() =>
        {
            Assert.That(context.MultiTurn, Is.True);
            Assert.That(context.Instruction, Is.Empty);
        });
    }

    [TestCase(null)]
    [TestCase("false")]
    [TestCase("nonsense")]
    public void NonTrueMultiTurnValues_KeepSingleTurnSemantics(string? multiTurn)
    {
        var context = AgentSessionContext.FromValues(
            "Do something", null, "/w", "/o", multiTurn: multiTurn);

        Assert.That(context.MultiTurn, Is.False);
    }

    [TestCase("console", true)]
    [TestCase("Console", true)]
    [TestCase(" console ", true)]
    [TestCase("advanced", false)]
    [TestCase(null, false)]
    [TestCase("nonsense", false)]
    public void ViewMode_ResolvesConsole_EverythingElseIsAdvanced(string? viewMode, bool isConsole)
    {
        var context = AgentSessionContext.FromValues(
            "Do something", null, "/w", "/o", viewMode: viewMode);

        Assert.Multiple(() =>
        {
            Assert.That(context.IsConsole, Is.EqualTo(isConsole));
            Assert.That(context.ViewMode,
                Is.EqualTo(isConsole ? AgentViewModes.Console : AgentViewModes.Advanced));
        });
    }

    [Test]
    public void ConsoleMode_MissingInstruction_IsAllowed()
    {
        var context = AgentSessionContext.FromValues(
            null, null, "/w", "/o", viewMode: AgentViewModes.Console);

        Assert.Multiple(() =>
        {
            Assert.That(context.IsConsole, Is.True);
            Assert.That(context.Instruction, Is.Empty);
        });
    }

    [Test]
    public void MissingDirectories_FallBackToCreatedTempDirectories()
    {
        var context = AgentSessionContext.FromValues("Do something", null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(context.WorkspaceDirectory), Is.True);
            Assert.That(Directory.Exists(context.OutputDirectory), Is.True);
        });

        Directory.Delete(context.WorkspaceDirectory);
        Directory.Delete(context.OutputDirectory);
    }
}
