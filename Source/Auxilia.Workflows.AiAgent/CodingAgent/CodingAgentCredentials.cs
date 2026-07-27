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
    /// <summary>Environment variables for a CLI child process: only the credential in use is exported.</summary>
    public IReadOnlyDictionary<string, string> ToEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (OAuthToken is { Length: > 0 })
            environment["CLAUDE_CODE_OAUTH_TOKEN"] = OAuthToken;
        else if (ApiKey is { Length: > 0 })
            environment["ANTHROPIC_API_KEY"] = ApiKey;
        return environment;
    }
}
