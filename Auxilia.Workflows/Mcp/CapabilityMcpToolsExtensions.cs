using Auxilia.AI;

namespace Auxilia.Workflows.Mcp;

public static class CapabilityMcpToolsExtensions
{
    /// <summary>
    /// Registers the tools exposed by <paramref name="tools"/> into the agent session builder.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="tools"/> has not been started.</exception>
    /// <exception cref="NotSupportedException">Thrown when the transport type carried by <paramref name="tools"/> is not supported.</exception>
    public static IAgentSessionBuilder WithCapabilityTools(
        this IAgentSessionBuilder builder,
        ICapabilityMcpTools tools)
    {
        if (!tools.IsStarted)
            throw new InvalidOperationException(
                $"Cannot register MCP tools for slot '{tools.SlotName}': the capability tools server has not been started. Call StartAsync before registering tools.");

        return tools.CurrentTransport switch
        {
            HttpMcpTransportConfig http =>
                builder.WithMcpServerToolWhitelist(http.EndpointUrl, http.McpServerName, tools.ToolNames.ToList()),
            _ => throw new NotSupportedException(
                $"Transport type '{tools.CurrentTransport?.GetType().Name}' is not supported by {nameof(CapabilityMcpToolsExtensions)}.{nameof(WithCapabilityTools)}.")
        };
    }
}
