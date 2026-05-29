namespace Auxilia.AI.AgenticFramework.Maf;

/// <summary>
/// Immutable descriptor of an MCP server to connect to when building a session.
/// </summary>
internal sealed record McpServerConfig(
    string Url,
    string McpName,
    IReadOnlyList<string>? Whitelist = null,
    IReadOnlyList<string>? Blacklist = null);
