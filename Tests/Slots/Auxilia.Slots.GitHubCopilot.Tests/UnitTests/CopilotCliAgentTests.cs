using Auxilia.Slots.GitHubCopilot;
using Auxilia.Workflows.AiAgent.CodingAgent;

namespace Auxilia.Slots.GitHubCopilot.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class CopilotCliAgentTests
{
    private static readonly CodingAgentRequest Request = new("Fix the bug", "/workspace");

    [Test]
    public void BuildStartInfo_PassesTokenOnlyThroughEnvironment_NeverAsArgument()
    {
        var agent = new CopilotCliAgent(new CopilotCliOptions { Token = "ghp-super-secret" });

        var startInfo = agent.BuildStartInfo(Request);

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.Environment["GH_TOKEN"], Is.EqualTo("ghp-super-secret"));
            Assert.That(startInfo.ArgumentList, Has.None.Contains("ghp-super-secret"));
            Assert.That(startInfo.FileName, Does.Not.Contain("ghp-super-secret"));
        });
    }

    [Test]
    public void BuildStartInfo_UsesHeadlessArguments_InTheWorkspace()
    {
        var agent = new CopilotCliAgent(new CopilotCliOptions { Token = "t", Model = "gpt-5" });

        var startInfo = agent.BuildStartInfo(Request);

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.ArgumentList, Does.Contain("-p"));
            Assert.That(startInfo.ArgumentList, Does.Contain("Fix the bug"));
            Assert.That(startInfo.ArgumentList, Does.Contain("--allow-all-tools"));
            Assert.That(startInfo.ArgumentList, Does.Contain("--model"));
            Assert.That(startInfo.ArgumentList, Does.Contain("gpt-5"));
            Assert.That(startInfo.WorkingDirectory, Is.EqualTo("/workspace"));
        });
    }

    [Test]
    public void Options_FromSettings_ReadTheLowerCaseTokenKey_AndDefaults()
    {
        var options = CopilotCliOptions.FromSettings(new Dictionary<string, string>
        {
            ["token"] = "abc",
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.Token, Is.EqualTo("abc"));
            Assert.That(options.CliPath, Is.EqualTo("copilot"));
            Assert.That(options.HasCredential, Is.True);
        });
    }

    [Test]
    public void SlotHandler_WithoutToken_RefusesRegistration()
    {
        var handler = new CopilotCliSlotHandler();
        Assert.Throws<InvalidOperationException>(() => handler.Register(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection(),
            "coding-agent", typeof(ICodingAgent),
            new SlotConfiguration("github-copilot-cli",
                new Dictionary<string, string>())));
    }

    [Test]
    public void RunAsync_MultiTurnInHeadlessMode_FailsLoudly()
    {
        var agent = new CopilotCliAgent(new CopilotCliOptions { Token = "t" });

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(
            Request with { MultiTurn = true }, (_, _) => Task.CompletedTask));
        Assert.That(ex!.Message, Does.Contain("UseSdkSession"),
            "The headless CLI is one-shot — multi-turn needs the SDK session protocol.");
    }

    [TestCase("- [ ] Write tests", "Write tests", "pending")]
    [TestCase("- [x] Fix build", "Fix build", "completed")]
    [TestCase("* [~] Refactor parser", "Refactor parser", "in_progress")]
    public void TryParseChecklistLine_ParsesMarkdownCheckboxes(string line, string content, string status)
    {
        var item = CopilotCliAgent.TryParseChecklistLine(line);

        Assert.That(item, Is.EqualTo(new AgentPlanItem(content, status)));
    }

    [TestCase("Running: ls")]
    [TestCase("Done - the task is complete.")]
    public void TryParseChecklistLine_IgnoresOrdinaryLines(string line)
    {
        Assert.That(CopilotCliAgent.TryParseChecklistLine(line), Is.Null);
    }
}
