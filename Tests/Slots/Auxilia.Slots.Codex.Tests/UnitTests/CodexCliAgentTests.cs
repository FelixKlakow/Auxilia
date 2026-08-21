using Auxilia.Slots.Codex;
using Auxilia.Workflows.AiAgent.CodingAgent;
using Auxilia.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.Codex.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class CodexCliAgentTests
{
    private static readonly CodingAgentRequest Request = new("Fix the bug", "/workspace");

    [Test]
    public void BuildStartInfo_PassesApiKeyOnlyThroughEnvironment_NeverAsArgument()
    {
        var agent = new CodexCliAgent(new CodexCliOptions { ApiKey = "sk-super-secret" });

        var startInfo = agent.BuildStartInfo(Request);

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.Environment["OPENAI_API_KEY"], Is.EqualTo("sk-super-secret"));
            Assert.That(startInfo.ArgumentList, Has.None.Contains("sk-super-secret"));
            Assert.That(startInfo.FileName, Does.Not.Contain("sk-super-secret"));
        });
    }

    [Test]
    public void BuildStartInfo_UsesExecArguments_InTheWorkspace()
    {
        var agent = new CodexCliAgent(new CodexCliOptions { ApiKey = "k", Model = "gpt-5-codex" });

        var startInfo = agent.BuildStartInfo(Request);

        Assert.Multiple(() =>
        {
            Assert.That(startInfo.ArgumentList[0], Is.EqualTo("exec"),
                "exec is the CLI's headless mode");
            Assert.That(startInfo.ArgumentList, Does.Contain("--dangerously-bypass-approvals-and-sandbox"),
                "the workflow container is the sandbox; nobody answers approval prompts headless");
            Assert.That(startInfo.ArgumentList, Does.Contain("--model"));
            Assert.That(startInfo.ArgumentList, Does.Contain("gpt-5-codex"));
            Assert.That(startInfo.ArgumentList[^1], Is.EqualTo("Fix the bug"),
                "the prompt is the trailing positional argument");
            Assert.That(startInfo.WorkingDirectory, Is.EqualTo("/workspace"));
        });
    }

    [Test]
    public void BuildStartInfo_BaseInstructions_RideThePrompt()
    {
        var agent = new CodexCliAgent(new CodexCliOptions { ApiKey = "k" });

        var startInfo = agent.BuildStartInfo(Request with { BaseInstructions = "Be terse." });

        Assert.That(startInfo.ArgumentList[^1], Is.EqualTo("Be terse.\n\nFix the bug"),
            "exec mode has no system-prompt seam — base instructions prepend the prompt");
    }

    [Test]
    public void Options_FromSettings_ReadKeysAndDefaults()
    {
        var options = CodexCliOptions.FromSettings(new Dictionary<string, string>
        {
            ["ApiKey"] = "abc",
        });

        Assert.Multiple(() =>
        {
            Assert.That(options.ApiKey, Is.EqualTo("abc"));
            Assert.That(options.CliPath, Is.EqualTo("codex"));
            Assert.That(options.HasCredential, Is.True);
        });
    }

    [Test]
    public void SlotHandler_WithoutACredential_Refuses()
    {
        var handler = new CodexCliSlotHandler();

        Assert.Throws<InvalidOperationException>(() => handler.Register(
            new ServiceCollection(), "coding-agent", typeof(ICodingAgent),
            new SlotConfiguration("codex-cli", new Dictionary<string, string>())));
    }

    [Test]
    public void SlotHandler_ExportsSessionCredentials_ForTheInteractiveConsole()
    {
        var services = new ServiceCollection();
        new CodexCliSlotHandler().Register(
            services, "coding-agent", typeof(ICodingAgent),
            new SlotConfiguration("codex-cli", new Dictionary<string, string> { ["ApiKey"] = "sk-1" }));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var credentials = scope.ServiceProvider.GetRequiredService<CodingAgentCredentials>();
        Assert.Multiple(() =>
        {
            Assert.That(credentials.CliPath, Is.EqualTo("codex"));
            Assert.That(credentials.ToEnvironment()["OPENAI_API_KEY"], Is.EqualTo("sk-1"));
            Assert.That(scope.ServiceProvider.GetRequiredService<ICodingAgent>(),
                Is.InstanceOf<CodexCliAgent>());
        });
    }
}
