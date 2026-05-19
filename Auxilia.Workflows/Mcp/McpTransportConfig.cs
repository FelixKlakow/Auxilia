namespace Auxilia.Workflows.Mcp;

/// <summary>Base for all transport variants. Subclasses carry the connection parameters for a specific transport mechanism.</summary>
public abstract record McpTransportConfig
{
    public abstract string McpServerName { get; init; }
}

/// <summary>HTTP transport: the MCP server listens on <see cref="EndpointUrl"/> (typically a loopback address).</summary>
public sealed record HttpMcpTransportConfig(string EndpointUrl, string McpServerName) : McpTransportConfig;

/// <summary>Named-pipe transport: the MCP server listens on a local named pipe identified by <see cref="PipeName"/>.</summary>
public sealed record NamedPipeMcpTransportConfig(string PipeName, string McpServerName) : McpTransportConfig;
