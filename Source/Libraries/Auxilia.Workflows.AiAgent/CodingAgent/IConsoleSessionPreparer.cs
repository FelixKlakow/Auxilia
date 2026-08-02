namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// Provider-specific preparation of an INTERACTIVE console session, run inside the container
/// right before the terminal host starts — e.g. materializing the slot's credential where the
/// CLI's interactive mode actually reads it. Registered by the slot handler; console mode runs
/// without one when the provider needs no preparation.
/// </summary>
public interface IConsoleSessionPreparer
{
    Task PrepareAsync(string workspaceDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Registers a workflow-hosted MCP server with the console CLI in the provider's native
    /// config (Claude: workspace <c>.mcp.json</c>; Copilot: <c>~/.copilot/mcp-config.json</c>).
    /// Providers without MCP support ignore it — the workflow's file contract still works.
    /// </summary>
    Task RegisterMcpServerAsync(
        string serverName, string endpointUrl, string workspaceDirectory,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
