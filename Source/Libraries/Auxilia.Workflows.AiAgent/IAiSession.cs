namespace Auxilia.Workflows.AiAgent;

/// <summary>A single stateful conversation context with an AI agent.</summary>
public interface IAiSession : IAsyncDisposable
{
    Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compacts the context window in place, retaining only information relevant to <paramref name="focusDescription"/>.
    /// Callers keep the same session reference; no new session is created.
    /// Providers that do not support native compaction must throw <see cref="NotSupportedException"/>.
    /// </summary>
    Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default);
}
