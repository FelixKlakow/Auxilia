namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// Configuration applied when opening an <see cref="IAiSession"/>.
/// </summary>
public sealed record AiSessionOptions
{
    /// <summary>
    /// System-level instructions injected before the first user turn.
    /// When <c>null</c>, the provider uses the slot's configured system prompt.
    /// </summary>
    public string? SystemPrompt { get; init; }
}
