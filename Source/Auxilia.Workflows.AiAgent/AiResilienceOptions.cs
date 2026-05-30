namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// Configures the bounded exponential-backoff retry behaviour of <see cref="ResilientAiAgent"/>.
/// </summary>
public sealed record AiResilienceOptions
{
    /// <summary>
    /// Maximum number of attempts, including the first. Must be at least 1. Defaults to 3.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>
    /// Delay in milliseconds before the second attempt. Each subsequent wait doubles
    /// (exponential back-off). Defaults to 1000 ms.
    /// </summary>
    public int InitialBackoffMs { get; init; } = 1000;

    /// <summary>
    /// When <c>true</c>, transient <see cref="IAiSession.ExecuteAsync"/> failures are also
    /// retried with the same back-off strategy. Defaults to <c>false</c>.
    /// </summary>
    public bool RetryExecute { get; init; } = false;
}
