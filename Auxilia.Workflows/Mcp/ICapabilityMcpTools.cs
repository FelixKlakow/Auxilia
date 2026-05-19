namespace Auxilia.Workflows.Mcp;

/// <summary>
/// Contract for a capability's in-process MCP tool server.
/// </summary>
/// <remarks>
/// Contract invariants:
/// <list type="bullet">
///   <item><description><see cref="IsStarted"/> is <c>false</c> before <see cref="StartAsync"/> is called.</description></item>
///   <item><description><see cref="CurrentTransport"/> is <c>null</c> before <see cref="StartAsync"/> is called.</description></item>
///   <item><description><see cref="ToolNames"/> returns an empty list when <see cref="IsStarted"/> is <c>false</c>.</description></item>
///   <item><description><see cref="StartAsync"/> is idempotent — calling it a second time with the same config is a no-op.</description></item>
///   <item><description>The MCP server must not start in the implementing class constructor.</description></item>
/// </list>
/// </remarks>
public interface ICapabilityMcpTools
{
    string SlotName { get; }
    bool IsStarted { get; }

    /// <summary>Non-null only when <see cref="IsStarted"/> is <c>true</c>.</summary>
    McpTransportConfig? CurrentTransport { get; }

    /// <summary>Empty when <see cref="IsStarted"/> is <c>false</c>.</summary>
    IReadOnlyList<string> ToolNames { get; }

    Task StartAsync(McpTransportConfig transport, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
