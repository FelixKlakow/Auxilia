using Auxilia.CodingSession.Workflow;

namespace Auxilia.CodingSession.Workflow.Tests;

[TestFixture]
[Category("Unit")]
public class SessionRunContextTests
{
    [Test]
    public void FromValues_UsesDefaults_WhenNothingIsConfigured()
    {
        var context = SessionRunContext.FromValues(null, null, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(context.SessionCommand, Is.EqualTo("claude"));
            Assert.That(context.TerminalPort, Is.EqualTo(7681));
            Assert.That(context.MaxDuration, Is.EqualTo(TimeSpan.FromMinutes(240)));
            Assert.That(context.BranchName, Does.StartWith("cc-session/"));
            Assert.That(Directory.Exists(context.WorkspaceDirectory), "a fallback workspace is created");
        });
    }

    [Test]
    public void FromValues_DerivesTheBranchFromTheInstanceId()
    {
        var context = SessionRunContext.FromValues(
            null, null, "claude-session-stub", "90", "b675ecc2-779e-4491-bcde-0fb5b4f4f787");

        Assert.Multiple(() =>
        {
            Assert.That(context.BranchName, Is.EqualTo("cc-session/b675ecc2"),
                "reruns of the same instance land on the same branch name");
            Assert.That(context.SessionCommand, Is.EqualTo("claude-session-stub"));
            Assert.That(context.MaxDuration, Is.EqualTo(TimeSpan.FromMinutes(90)));
        });
    }
}
