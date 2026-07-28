using System.Text.Json;
using System.Text.Json.Nodes;
using Auxilia.Workflows.AiAgent.CodingAgent;

namespace Auxilia.Slots.ClaudeCode;

/// <summary>
/// Console-mode login for the Claude Code CLI: interactive sessions read
/// <c>~/.claude/.credentials.json</c> and IGNORE the <c>CLAUDE_CODE_OAUTH_TOKEN</c> environment
/// variable (that one only covers headless use), so the JIT-delivered account token is written
/// there before the terminal host starts — inside the run's container, which by design holds
/// the credential anyway. The first-run wizard is skipped so the operator lands in the session.
/// An API-key fallback needs no file; it rides the environment.
/// </summary>
public sealed class ClaudeInteractiveLogin(CodingAgentCredentials credentials, TimeProvider? time = null)
    : IConsoleSessionPreparer
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The CLI's home; overridable for tests.</summary>
    public string HomeDirectory { get; init; } =
        Environment.GetEnvironmentVariable("HOME")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public async Task PrepareAsync(string workspaceDirectory, CancellationToken cancellationToken)
    {
        if (credentials.OAuthToken is not { Length: > 0 } token)
            return;

        var claudeDirectory = Path.Combine(HomeDirectory, ".claude");
        Directory.CreateDirectory(claudeDirectory);

        // A setup-token is long-lived and has no refresh token; the far expiry keeps the CLI
        // from attempting a refresh mid-session.
        var wire = new CredentialsFile(new ClaudeAiOauth(
            token, "",
            _time.GetUtcNow().AddDays(365).ToUnixTimeMilliseconds(),
            ["user:inference", "user:profile"]));
        await File.WriteAllTextAsync(
            Path.Combine(claudeDirectory, ".credentials.json"),
            JsonSerializer.Serialize(wire), cancellationToken);

        // The image bake (CLI installer) already leaves a ~/.claude.json — MERGE the
        // onboarding flags into whatever exists, or the first-run wizard still shows.
        var configPath = Path.Combine(HomeDirectory, ".claude.json");
        var config = File.Exists(configPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(configPath, cancellationToken)) as JsonObject
              ?? new JsonObject()
            : new JsonObject();
        config["hasCompletedOnboarding"] = true;
        config["theme"] = config["theme"]?.GetValue<string>() is { Length: > 0 } theme ? theme : "dark";
        // The workspace was DELIBERATELY mounted by the run's configuration — the operator
        // already chose it, so the folder-trust question is answered; the isolated container
        // stays the safety net.
        var projects = config["projects"] as JsonObject ?? new JsonObject();
        var project = projects[workspaceDirectory] as JsonObject ?? new JsonObject();
        project["hasTrustDialogAccepted"] = true;
        projects[workspaceDirectory] = project;
        config["projects"] = projects;
        await File.WriteAllTextAsync(configPath, config.ToJsonString(), cancellationToken);
    }

    /// <summary>The CLI's on-disk credential shape (camelCase by contract).</summary>
    private sealed record CredentialsFile(ClaudeAiOauth claudeAiOauth);

    private sealed record ClaudeAiOauth(
        string accessToken, string refreshToken, long expiresAt, string[] scopes);
}
