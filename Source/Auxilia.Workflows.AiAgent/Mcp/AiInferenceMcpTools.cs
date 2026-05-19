using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Auxilia.Workflows.Mcp;

namespace Auxilia.Workflows.AiAgent.Mcp;

/// <summary>
/// MCP tool server for <see cref="IAiAgent"/>.
/// Exposes AI inference as a prefixed MCP tool under the given slot name.
/// </summary>
public sealed class AiInferenceMcpTools : CapabilityMcpToolsBase
{
    public AiInferenceMcpTools(
        string slotName,
        IAiAgent agent,
        ILoggerFactory? loggerFactory = null)
        : base(slotName, BuildOptions(slotName, agent), loggerFactory)
    {
    }

    private static McpServerOptions BuildOptions(string slotName, IAiAgent agent)
    {
        var options = new McpServerOptions
        {
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(),
            ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = slotName,
                Version = "1.0"
            }
        };

        options.ToolCollection.Add(McpServerTool.Create(
            async (string prompt, CancellationToken ct) =>
            {
                var session = await agent.OpenSessionAsync(null, ct);
                await using (session)
                {
                    return await session.ExecuteAsync(prompt, ct);
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "run_inference"),
                Description = "Runs AI inference with the given prompt and returns the plain-text response."
            }));

        return options;
    }
}
