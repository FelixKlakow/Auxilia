using Auxilia.ClaudeCode.Workflow;
using Auxilia.Slots.ClaudeCode;
using Auxilia.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.Slots.ClaudeCode.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class ClaudeCodeCliSlotHandlerTests
{
    [Test]
    public void CodingAgentSlot_RegistersCliAgent()
    {
        var services = new ServiceCollection();
        var handler = new ClaudeCodeCliSlotHandler();

        handler.Register(services, "coding-agent", typeof(ICodingAgent),
            new SlotConfiguration("claude-code-cli", new Dictionary<string, string>
            {
                ["ApiKey"] = "sk-test"
            }));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.That(scope.ServiceProvider.GetRequiredService<ICodingAgent>(),
            Is.InstanceOf<ClaudeCodeCliAgent>());
    }

    [Test]
    public void CodingAgentSlot_WithOnlyAccountToken_RegistersCliAgent()
    {
        var services = new ServiceCollection();
        var handler = new ClaudeCodeCliSlotHandler();

        handler.Register(services, "coding-agent", typeof(ICodingAgent),
            new SlotConfiguration("claude-code-cli", new Dictionary<string, string>
            {
                ["OAuthToken"] = "sk-ant-oat-test"
            }));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.That(scope.ServiceProvider.GetRequiredService<ICodingAgent>(),
            Is.InstanceOf<ClaudeCodeCliAgent>());
    }

    [Test]
    public void MissingCredential_FailsRegistration()
    {
        var handler = new ClaudeCodeCliSlotHandler();

        Assert.Throws<InvalidOperationException>(() => handler.Register(
            new ServiceCollection(), "coding-agent", typeof(ICodingAgent),
            new SlotConfiguration("claude-code-cli", new Dictionary<string, string>())));
    }

    [Test]
    public void UnknownSlot_Throws()
    {
        var handler = new ClaudeCodeCliSlotHandler();

        Assert.Throws<InvalidOperationException>(() => handler.Register(
            new ServiceCollection(), "no-such-slot", typeof(ICodingAgent),
            new SlotConfiguration("claude-code-cli", new Dictionary<string, string>())));
    }

    [Test]
    public void OptionsFromSettings_AppliesDefaultsAndOverrides()
    {
        var defaults = ClaudeCodeCliOptions.FromSettings(new Dictionary<string, string>
        {
            ["ApiKey"] = "sk-test"
        });
        var overridden = ClaudeCodeCliOptions.FromSettings(new Dictionary<string, string>
        {
            ["ApiKey"] = "sk-test",
            ["CliPath"] = "/usr/local/bin/claude-stub",
            ["Model"] = "claude-sonnet-4-6",
            ["MaxTurns"] = "5"
        });

        Assert.Multiple(() =>
        {
            Assert.That(defaults.CliPath, Is.EqualTo("claude"));
            Assert.That(defaults.Model, Is.Null);
            Assert.That(defaults.MaxTurns, Is.Null, "no cap unless explicitly configured");
            Assert.That(overridden.CliPath, Is.EqualTo("/usr/local/bin/claude-stub"));
            Assert.That(overridden.Model, Is.EqualTo("claude-sonnet-4-6"));
            Assert.That(overridden.MaxTurns, Is.EqualTo(5));
        });
    }

    [Test]
    public void OptionsFromSettings_InvalidMaxTurns_MeansNoCap()
    {
        var options = ClaudeCodeCliOptions.FromSettings(new Dictionary<string, string>
        {
            ["ApiKey"] = "sk-test",
            ["MaxTurns"] = "not-a-number"
        });

        Assert.That(options.MaxTurns, Is.Null);
    }

    [Test]
    public void OptionsFromSettings_CredentialPrecedence()
    {
        var both = ClaudeCodeCliOptions.FromSettings(new Dictionary<string, string>
        {
            ["OAuthToken"] = "sk-ant-oat-test",
            ["ApiKey"] = "sk-test"
        });
        var neither = ClaudeCodeCliOptions.FromSettings(new Dictionary<string, string>());

        Assert.Multiple(() =>
        {
            Assert.That(both.HasCredential, Is.True);
            Assert.That(both.OAuthToken, Is.EqualTo("sk-ant-oat-test"));
            Assert.That(neither.HasCredential, Is.False);
        });
    }
}
