namespace Auxilia.Workflows.AiAgent;

/// <summary>
/// An active AI agent session. Configure MCP server tools before calling <see cref="ExecuteAsync"/>.
/// Dispose when the session is no longer needed.
/// </summary>
public interface IAiAgentSession : IDisposable
{
    /// <summary>Registers an MCP server whose tools are made available to the agent during execution.</summary>
    IAiAgentSession WithMcpServerTools(string serverUrl, string serverName);

    /// <summary>Sends <paramref name="prompt"/> to the agent and returns the response text.</summary>
    Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default);
}
