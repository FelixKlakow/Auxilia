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
    public void TmuxStartInfo_CarriesTheCredentialOnlyInTheEnvironment()
    {
        var context = SessionRunContext.FromValues("/workspace", "/output", "claude", "60", "run-1") with
        {
            SessionEnvironment = new Dictionary<string, string>
            {
                ["CLAUDE_CODE_OAUTH_TOKEN"] = "sk-ant-oat-secret"
            }
        };

        var startInfo = TmuxSessionHost.BuildTmuxStartInfo(context);

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.Environment["CLAUDE_CODE_OAUTH_TOKEN"], Is.EqualTo("sk-ant-oat-secret"));
            Assert.That(startInfo.Arguments, Does.Not.Contain("sk-ant-oat-secret"),
                "the credential must never appear on the command line");
            Assert.That(startInfo.Arguments, Does.Contain("claude; tmux kill-server"),
                "the CLI's exit still tears the session down");
            Assert.That(startInfo.Arguments, Does.Contain("-c \"/workspace\""));
        });
    }
}
