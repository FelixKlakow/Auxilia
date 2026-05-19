namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// Configuration applied when opening an <see cref="IAiSession"/>.
/// All properties are optional; unset values are resolved by the provider using its registered defaults.
/// </summary>
public sealed record AiSessionOptions
{
    /// <summary>
    /// System-level instructions injected before the first user turn.
    /// When <c>null</c>, the provider uses the slot's configured system prompt.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Overrides the model configured for the slot.
    /// When <c>null</c>, the slot's default model is used.
    /// </summary>
    public string? ModelOverride { get; init; }

    /// <summary>
    /// Maximum number of output tokens the model may generate per turn.
    /// When <c>null</c>, the provider's default applies.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Sampling temperature in [0.0, 2.0]. Lower values produce more deterministic output.
    /// When <c>null</c>, the provider's default applies.
    /// </summary>
    public float? Temperature { get; init; }
}
