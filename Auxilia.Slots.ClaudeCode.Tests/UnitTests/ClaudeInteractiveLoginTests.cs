using System.Text.Json;
using Auxilia.Workflows.AiAgent.CodingAgent;

namespace Auxilia.Slots.ClaudeCode.Tests.UnitTests;

[TestFixture, Category("Unit")]
public sealed class ClaudeInteractiveLoginTests
{
    private string _home = null!;

    [SetUp]
    public void SetUp()
    {
        _home = Path.Combine(Path.GetTempPath(), $"claude-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_home, recursive: true);

    [Test]
    public async Task Prepare_WritesTheCredentialsFile_WhereInteractiveLoginReadsIt()
    {
        var login = new ClaudeInteractiveLogin(
            new CodingAgentCredentials("sk-ant-oat-secret", null, "claude", null))
        {
            HomeDirectory = _home
        };

        await login.PrepareAsync("/workspace", CancellationToken.None);

        var credentialsPath = Path.Combine(_home, ".claude", ".credentials.json");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(credentialsPath));
        var oauth = json.RootElement.GetProperty("claudeAiOauth");
        Assert.Multiple(() =>
        {
            Assert.That(oauth.GetProperty("accessToken").GetString(), Is.EqualTo("sk-ant-oat-secret"));
            Assert.That(oauth.GetProperty("expiresAt").GetInt64(),
                Is.GreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                "a setup-token is long-lived — the CLI must not try to refresh it mid-session");
            Assert.That(File.ReadAllText(Path.Combine(_home, ".claude.json")),
                Does.Contain("hasCompletedOnboarding"),
                "the operator lands in the session, not the first-run wizard");
        });
    }

    [Test]
    public async Task Prepare_ApiKeyFallback_WritesNothing()
    {
        var login = new ClaudeInteractiveLogin(
            new CodingAgentCredentials(null, "sk-key", "claude", null))
        {
            HomeDirectory = _home
        };

        await login.PrepareAsync("/workspace", CancellationToken.None);

        Assert.That(Directory.Exists(Path.Combine(_home, ".claude")), Is.False,
            "an API key rides the process environment — no credential file exists to write");
    }

    [Test]
    public async Task Prepare_MergesOnboardingFlags_IntoTheImageBakedConfig()
    {
        // The CLI installer leaves a config behind in the image — preparation must merge,
        // never replace, or the wizard shows anyway (and installer state would be lost).
        await File.WriteAllTextAsync(
            Path.Combine(_home, ".claude.json"),
            "{\"installMethod\":\"native\",\"theme\":\"light\"}");
        var login = new ClaudeInteractiveLogin(
            new CodingAgentCredentials("sk-ant-oat-secret", null, "claude", null))
        {
            HomeDirectory = _home
        };

        await login.PrepareAsync("/workspace", CancellationToken.None);

        using var config = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_home, ".claude.json")));
        Assert.Multiple(() =>
        {
            Assert.That(config.RootElement.GetProperty("hasCompletedOnboarding").GetBoolean(), Is.True);
            Assert.That(config.RootElement.GetProperty("installMethod").GetString(), Is.EqualTo("native"));
            Assert.That(config.RootElement.GetProperty("theme").GetString(), Is.EqualTo("light"),
                "an already-chosen theme wins over the default");
            Assert.That(config.RootElement.GetProperty("projects").GetProperty("/workspace")
                    .GetProperty("hasTrustDialogAccepted").GetBoolean(), Is.True,
                "the deliberately mounted workspace is pre-trusted — the operator already chose it");
        });
    }
}
