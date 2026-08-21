namespace Auxilia.Slots.Codex;

/// <summary>Typed view of the "coding-agent" slot settings delivered at JIT activation.</summary>
public sealed record CodexCliOptions
{
    public const string DefaultCliPath = "codex";

    /// <summary>
    /// OpenAI API key — reaches the CLI only via its process environment
    /// (<c>OPENAI_API_KEY</c>), never on the command line.
    /// </summary>
    public string? ApiKey { get; init; }

    public string CliPath { get; init; } = DefaultCliPath;

    /// <summary>Optional model override; the CLI's default model is used when unset.</summary>
    public string? Model { get; init; }

    public bool HasCredential => !string.IsNullOrWhiteSpace(ApiKey);

    public static CodexCliOptions FromSettings(IReadOnlyDictionary<string, string> settings) => new()
    {
        ApiKey = settings.GetValueOrDefault("ApiKey") is { Length: > 0 } key ? key : null,
        CliPath = settings.GetValueOrDefault("CliPath") is { Length: > 0 } path ? path : DefaultCliPath,
        Model = settings.GetValueOrDefault("Model") is { Length: > 0 } model ? model : null,
    };
}
