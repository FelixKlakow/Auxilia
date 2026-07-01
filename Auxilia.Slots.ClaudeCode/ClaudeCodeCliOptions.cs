namespace Auxilia.Slots.ClaudeCode;

/// <summary>Typed view of the "coding-agent" slot settings delivered at JIT activation.</summary>
public sealed record ClaudeCodeCliOptions
{
    public const string DefaultCliPath = "claude";
    public const int DefaultMaxTurns = 25;

    /// <summary>Anthropic API key; reaches the CLI only via its process environment.</summary>
    public required string ApiKey { get; init; }

    public string CliPath { get; init; } = DefaultCliPath;

    /// <summary>Optional model override; the CLI's default model is used when unset.</summary>
    public string? Model { get; init; }

    public int MaxTurns { get; init; } = DefaultMaxTurns;

    public static ClaudeCodeCliOptions FromSettings(IReadOnlyDictionary<string, string> settings) => new()
    {
        ApiKey = settings.GetValueOrDefault("ApiKey", string.Empty),
        CliPath = settings.GetValueOrDefault("CliPath") is { Length: > 0 } path ? path : DefaultCliPath,
        Model = settings.GetValueOrDefault("Model") is { Length: > 0 } model ? model : null,
        MaxTurns = int.TryParse(settings.GetValueOrDefault("MaxTurns"), out var turns) && turns > 0
            ? turns
            : DefaultMaxTurns
    };
}
