using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.CodingSession.Workflow;

namespace Auxilia.CodingSession.Workflow.Tests;

[TestFixture, Category("Unit")]
public sealed class SessionCredentialTests
{
    [Test]
    public void ToEnvironment_AccountTokenWinsOverApiKey_OnlyOneExported()
    {
        var both = new CodingAgentCredentials("sk-ant-oat-token", "sk-key", "claude", null);
        var keyOnly = new CodingAgentCredentials(null, "sk-key", "claude", null);
        var none = new CodingAgentCredentials(null, null, "claude", null);

        Assert.Multiple(() =>
        {
            Assert.That(both.ToEnvironment(),
                Is.EqualTo(new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "sk-ant-oat-token" }));
            Assert.That(keyOnly.ToEnvironment(),
                Is.EqualTo(new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-key" }));
            Assert.That(none.ToEnvironment(), Is.Empty);
        });
    }

    [Test]
    public void TmuxStartInfo_CarriesTheCredentialAsASessionVariable_NeverInsideTheCommand()
    {
        var context = SessionRunContext.FromValues("/workspace", "/output", "claude", "60", "run-1") with
        {
            SessionEnvironment = new Dictionary<string, string>
            {
                ["CLAUDE_CODE_OAUTH_TOKEN"] = "sk-ant-oat-secret"
            }
        };

        var startInfo = TmuxSessionHost.BuildTmuxStartInfo(
            new TerminalSessionInfo(context.WorkspaceDirectory, context.SessionCommand, context.TerminalPort)
            {
                Environment = context.SessionEnvironment
            });

        var arguments = startInfo.ArgumentList;
        Assert.Multiple(() =>
        {
            var e = arguments.IndexOf("-e");
            Assert.That(e, Is.GreaterThan(0), "the credential is a per-session tmux variable (-e)");
            Assert.That(arguments[e + 1], Is.EqualTo("CLAUDE_CODE_OAUTH_TOKEN=sk-ant-oat-secret"));
            Assert.That(arguments[^1], Is.EqualTo("claude; tmux kill-server"),
                "the CLI's exit still tears the session down; the command never carries the credential");
            startInfo.Environment.TryGetValue("CLAUDE_CODE_OAUTH_TOKEN", out var inherited);
            Assert.That(inherited, Is.Not.EqualTo("sk-ant-oat-secret"),
                "the client environment is not the delivery path — only the first session would inherit it");
            Assert.That(startInfo.ArgumentList, Does.Contain("/workspace"),
                "the session starts in the workspace");
        });
    }
}
