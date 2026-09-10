using Auxilia.Workflows.AiAgent.CodingAgent;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public sealed class TmuxSessionHostTests
{
    [Test]
    public void BuildTmuxStartInfo_SecondarySession_GetsItsOwnVariables_PerSession()
    {
        // Only the FIRST new-session forks the tmux server and inherits the client environment;
        // a secondary session (the reviewer console) must receive its variables explicitly.
        var reviewer = new TerminalSessionInfo("/workspace", "claude -p \"review\"", 7681)
        {
            SessionName = "reviewer",
            ServeTerminal = false,
            EndsServerOnExit = false,
            Environment = new Dictionary<string, string>
            {
                ["ANTHROPIC_API_KEY"] = "sk-reviewer",
                ["CLAUDE_CONFIG_DIR"] = "/tmp/reviewer",
            },
        };

        var startInfo = TmuxSessionHost.BuildTmuxStartInfo(reviewer);
        var arguments = startInfo.ArgumentList;

        Assert.Multiple(() =>
        {
            Assert.That(arguments.Take(6),
                Is.EqualTo(new[] { "new-session", "-d", "-s", "reviewer", "-c", "/workspace" }));
            Assert.That(arguments.Skip(6).Take(4),
                Is.EqualTo(new[] { "-e", "ANTHROPIC_API_KEY=sk-reviewer", "-e", "CLAUDE_CONFIG_DIR=/tmp/reviewer" }),
                "every variable travels as a -e assignment before the command");
            Assert.That(arguments[^1], Is.EqualTo("claude -p \"review\""),
                "a secondary session never chains kill-server");
            startInfo.Environment.TryGetValue("ANTHROPIC_API_KEY", out var inherited);
            Assert.That(inherited, Is.Not.EqualTo("sk-reviewer"),
                "the client environment is not relied on");
        });
    }

    [Test]
    public void BuildTmuxStartInfo_WithoutVariables_HasNoEnvironmentFlags()
    {
        var startInfo = TmuxSessionHost.BuildTmuxStartInfo(new TerminalSessionInfo("/workspace", "bash", 7681));

        Assert.That(startInfo.ArgumentList, Is.EqualTo(new[]
        {
            "new-session", "-d", "-s", "agent-session", "-c", "/workspace", "bash; tmux kill-server"
        }));
    }

    [Test]
    public void DescribeForLog_RedactsEverySessionVariableValue()
    {
        var startInfo = TmuxSessionHost.BuildTmuxStartInfo(new TerminalSessionInfo("/workspace", "claude", 7681)
        {
            Environment = new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "sk-ant-oat-secret" },
        });

        var described = TmuxSessionHost.DescribeForLog(startInfo);

        Assert.Multiple(() =>
        {
            Assert.That(described, Does.Not.Contain("sk-ant-oat-secret"));
            Assert.That(described, Does.Contain("-e CLAUDE_CODE_OAUTH_TOKEN=<redacted>"));
            Assert.That(described, Does.StartWith("tmux new-session -d -s agent-session -c /workspace"));
        });
    }
}
