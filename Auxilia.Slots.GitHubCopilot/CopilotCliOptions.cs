namespace Auxilia.Slots.GitHubCopilot;

/// <summary>Typed view of the "coding-agent" slot settings delivered at JIT activation.</summary>
public sealed record CopilotCliOptions
{
    public const string DefaultCliPath = "copilot";

    /// <summary>
    /// GitHub token with Copilot access — reaches the CLI only via its process environment
    /// (<c>GH_TOKEN</c>), never on the command line.
    /// </summary>
    public string? Token { get; init; }

    public string CliPath { get; init; } = DefaultCliPath;

    /// <summary>Optional model override; the CLI's default model is used when unset.</summary>
    public string? Model { get; init; }

    public bool HasCredential => !string.IsNullOrWhiteSpace(Token);

    public static CopilotCliOptions FromSettings(IReadOnlyDictionary<string, string> settings) => new()
    {
        Token = settings.GetValueOrDefault("token") is { Length: > 0 } token ? token : null,
        CliPath = settings.GetValueOrDefault("CliPath") is { Length: > 0 } path ? path : DefaultCliPath,
        Model = settings.GetValueOrDefault("Model") is { Length: > 0 } model ? model : null
    };
}
