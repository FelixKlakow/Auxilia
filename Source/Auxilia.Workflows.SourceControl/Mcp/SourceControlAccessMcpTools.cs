using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Auxilia.Workflows.Mcp;

namespace Auxilia.Workflows.SourceControl.Mcp;

/// <summary>
/// MCP tool server for <see cref="ISourceControlAccess"/>.
/// Exposes source-control operations as prefixed MCP tools under the given slot name.
/// </summary>
public sealed class SourceControlAccessMcpTools : CapabilityMcpToolsBase
{
    public SourceControlAccessMcpTools(
        string slotName,
        ISourceControlAccess access,
        ILoggerFactory? loggerFactory = null)
        : base(slotName, BuildOptions(slotName, access), loggerFactory)
    {
    }

    private static McpServerOptions BuildOptions(string slotName, ISourceControlAccess access)
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
            async (string? relativePath, CancellationToken ct) =>
                string.Join("\n", await access.ListFilesAsync(relativePath, ct)),
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "list_files"),
                Description = "Lists files in the repository. Returns one path per line."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string relativePath, CancellationToken ct) =>
                await access.ReadFileContentAsync(relativePath, ct),
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "read_file"),
                Description = "Reads and returns the content of a file in the repository."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string baseRef, string headRef, CancellationToken ct) =>
            {
                var files = await access.GetChangedFilesAsync(baseRef, headRef, ct);
                return JsonSerializer.Serialize(
                    files.Select(f => new { filePath = f.RelativePath, kind = f.Kind.ToString() }));
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_changed_files"),
                Description = "Returns a JSON array of files changed between two refs."
            }));

        return options;
    }
}
