namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// Behavioral contract for AI invocation inside a workflow.
/// Provider packages implement this interface. Workflow code depends only on this contract,
/// never on any specific AI SDK.
/// </summary>
public interface IAiAgent
{
    Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default);
}
