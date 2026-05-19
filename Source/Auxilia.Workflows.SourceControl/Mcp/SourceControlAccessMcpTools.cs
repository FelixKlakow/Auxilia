using System;
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
        : base(slotName, BuildOptions(slotName, access, loggerFactory), loggerFactory)
    {
    }

    private static McpServerOptions BuildOptions(string slotName, ISourceControlAccess access, ILoggerFactory? loggerFactory)
    {
        var logger = loggerFactory?.CreateLogger<SourceControlAccessMcpTools>();

        var options = new McpServerOptions
        {
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>(),
            ResourceCollection = new McpServerResourceCollection(),
            ServerInfo = new ModelContextProtocol.Protocol.Implementation
            {
                Name = slotName,
                Version = "1.0"
            }
        };

        options.ToolCollection.Add(McpServerTool.Create(
            async (string? relativePath, CancellationToken ct) =>
            {
                try
                {
                    return string.Join(global::System.Environment.NewLine, await access.ListFilesAsync(relativePath, ct));
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in list_files");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "list_files"),
                Description = "Lists files in the repository. Returns one path per line."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string relativePath, CancellationToken ct) =>
            {
                try
                {
                    return await access.ReadFileContentAsync(relativePath, ct);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in read_file");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "read_file"),
                Description = "Reads and returns the content of a file in the repository."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string baseRef, string headRef, CancellationToken ct) =>
            {
                try
                {
                    var files = await access.GetChangedFilesAsync(baseRef, headRef, ct);
                    return JsonSerializer.Serialize(
                        files.Select(f => new { relativePath = f.RelativePath, kind = f.Kind.ToString() }),
                        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in get_changed_files");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_changed_files"),
                Description = "Returns a JSON array of files changed between two refs."
            }));

        options.ResourceCollection.Add(McpServerResource.Create(
            () => access.WorkingPath,
            new McpServerResourceCreateOptions
            {
                UriTemplate = "scm://working-path",
                Name = "working-path",
                Description = "The working path of the source control repository.",
                MimeType = "text/plain"
            }));

        return options;
    }
}
