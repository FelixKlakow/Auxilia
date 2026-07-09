namespace Auxilia.Slots.ClaudeCode;

/// <summary>Typed view of the "coding-agent" slot settings delivered at JIT activation.</summary>
public sealed record ClaudeCodeCliOptions
{
    public const string DefaultCliPath = "claude";

    /// <summary>
    /// Claude account token (from 'claude setup-token') — the preferred credential; reaches
    /// the CLI only via its process environment.
    /// </summary>
    public string? OAuthToken { get; init; }

    /// <summary>Anthropic API key — the fallback when no account is connected; environment-only, like the token.</summary>
    public string? ApiKey { get; init; }

    public string CliPath { get; init; } = DefaultCliPath;

    /// <summary>Optional model override; the CLI's default model is used when unset.</summary>
    public string? Model { get; init; }

    /// <summary>Optional cap on agent turns per run; unset (default) lets the agent finish its task.</summary>
    public int? MaxTurns { get; init; }

    public bool HasCredential
        => !string.IsNullOrWhiteSpace(OAuthToken) || !string.IsNullOrWhiteSpace(ApiKey);

    public static ClaudeCodeCliOptions FromSettings(IReadOnlyDictionary<string, string> settings) => new()
    {
        OAuthToken = settings.GetValueOrDefault("OAuthToken") is { Length: > 0 } token ? token : null,
        ApiKey = settings.GetValueOrDefault("ApiKey") is { Length: > 0 } key ? key : null,
        CliPath = settings.GetValueOrDefault("CliPath") is { Length: > 0 } path ? path : DefaultCliPath,
        Model = settings.GetValueOrDefault("Model") is { Length: > 0 } model ? model : null,
        MaxTurns = int.TryParse(settings.GetValueOrDefault("MaxTurns"), out var turns) && turns > 0
            ? turns
            : null
    };
}
