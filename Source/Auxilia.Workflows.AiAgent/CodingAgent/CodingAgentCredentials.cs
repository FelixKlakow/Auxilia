namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// The credentials and CLI coordinates a "coding-agent" slot provider delivers, for
/// workflows that run the CLI themselves (e.g. the interactive coding session) instead of
/// going through <see cref="ICodingAgent"/>. Values must only ever reach the CLI via its
/// process environment — never as arguments, never in logs.
/// </summary>
public sealed record CodingAgentCredentials(
    string? OAuthToken,
    string? ApiKey,
    string CliPath,
    string? Model)
{
    /// <summary>
    /// Provider-specific credential environment overriding the Claude-shaped default —
    /// e.g. the Copilot handler exports its GitHub token as <c>GH_TOKEN</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentOverrides { get; init; }

    /// <summary>
    /// Extra CLI arguments for an UNATTENDED driven console (nobody answers permission
    /// prompts; the isolated container is the safety net) — e.g. Claude's
    /// <c>--dangerously-skip-permissions</c>. Null = the provider needs none.
    /// </summary>
    public string? UnattendedCliArguments { get; init; }

    /// <summary>The CLI's context-compaction command (e.g. <c>/compact</c>); null = unsupported.</summary>
    public string? CompactCommand { get; init; }

    /// <summary>Environment variables for a CLI child process: only the credential in use is exported.</summary>
    public IReadOnlyDictionary<string, string> ToEnvironment()
    {
        if (EnvironmentOverrides is not null)
            return EnvironmentOverrides;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (OAuthToken is { Length: > 0 })
            environment["CLAUDE_CODE_OAUTH_TOKEN"] = OAuthToken;
        else if (ApiKey is { Length: > 0 })
            environment["ANTHROPIC_API_KEY"] = ApiKey;
        return environment;
    }
}
