namespace Auxilia.Workflows.AiAgent;

/// <summary>A single stateful conversation context with an AI agent.</summary>
public interface IAiSession : IAsyncDisposable
{
    Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default);
}
