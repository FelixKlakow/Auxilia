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

    /// <summary>
    /// When <see cref="Token"/> expires, if the connector says so (setting
    /// <c>token_expires_at</c>: ISO 8601, or Unix seconds / milliseconds — written by whoever
    /// writes the token, or by the Core's OAuth refresh). Unknown = treated as long-lived.
    /// </summary>
    public DateTimeOffset? TokenExpiresAt { get; init; }

    public string CliPath { get; init; } = DefaultCliPath;

    /// <summary>Optional model override; the CLI's default model is used when unset.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Run through the Copilot SDK's session protocol (interactive parity: permissions,
    /// questions, guidance, model switch). False = the plain headless line mode (stubs, tests).
    /// </summary>
    public bool UseSdkSession { get; init; }

    public bool HasCredential => !string.IsNullOrWhiteSpace(Token);

    public static CopilotCliOptions FromSettings(IReadOnlyDictionary<string, string> settings) => new()
    {
        Token = settings.GetValueOrDefault("token") is { Length: > 0 } token ? token : null,
        TokenExpiresAt = ParseInstant(settings.GetValueOrDefault(CopilotTokenSource.ExpiresAtKey)),
        CliPath = settings.GetValueOrDefault("CliPath") is { Length: > 0 } path ? path : DefaultCliPath,
        Model = settings.GetValueOrDefault("Model") is { Length: > 0 } model ? model : null,
        UseSdkSession = string.Equals(
            settings.GetValueOrDefault("UseSdkSession"), "true", StringComparison.OrdinalIgnoreCase)
    };

    /// <summary>An instant as ISO 8601 or Unix time (seconds, or milliseconds beyond 10^11).</summary>
    internal static DateTimeOffset? ParseInstant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (long.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var unix))
            return unix > 100_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                : DateTimeOffset.FromUnixTimeSeconds(unix);
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var instant)
            ? instant
            : null;
    }
}
