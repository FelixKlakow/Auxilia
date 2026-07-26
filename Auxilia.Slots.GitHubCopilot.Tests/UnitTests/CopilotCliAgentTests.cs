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
}
