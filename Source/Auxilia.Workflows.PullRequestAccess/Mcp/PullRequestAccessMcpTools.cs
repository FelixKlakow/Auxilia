using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Auxilia.Workflows.Mcp;

namespace Auxilia.Workflows.PullRequestAccess.Mcp;

/// <summary>
/// MCP tool server for <see cref="IPullRequestAccess"/>.
/// Exposes pull-request host operations as prefixed MCP tools under the given slot name.
/// </summary>
public sealed class PullRequestAccessMcpTools : CapabilityMcpToolsBase
{
    public PullRequestAccessMcpTools(
        string slotName,
        IPullRequestAccess access,
        ILoggerFactory? loggerFactory = null)
        : base(slotName, BuildOptions(slotName, access), loggerFactory)
    {
    }

    private static McpServerOptions BuildOptions(string slotName, IPullRequestAccess access)
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
            async (CancellationToken ct) =>
            {
                var files = await access.GetChangedFilesAsync(ct);
                return JsonSerializer.Serialize(
                    files.Select(f => new { filePath = f.RelativePath, kind = f.Kind.ToString() }));
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_changed_files"),
                Description = "Returns a JSON array of files changed in this pull request."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string filePath, CancellationToken ct) =>
            {
                var hunks = await access.GetDiffHunksAsync(filePath, ct);
                return JsonSerializer.Serialize(hunks.Select(h => new
                {
                    filePath = h.FilePath,
                    oldStart = h.OldStart,
                    oldCount = h.OldCount,
                    newStart = h.NewStart,
                    newCount = h.NewCount,
                    content = h.Content
                }));
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_diff_hunks"),
                Description = "Returns a JSON array of diff hunks for the specified file."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (CancellationToken ct) =>
            {
                var comments = await access.GetCommentsAsync(ct);
                return JsonSerializer.Serialize(comments.Select(c => new
                {
                    id = c.Id,
                    body = c.Body,
                    author = c.Author,
                    filePath = c.FilePath,
                    lineNumber = c.LineNumber,
                    createdAt = c.CreatedAt
                }));
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_comments"),
                Description = "Returns a JSON array of review comments on this pull request."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (CancellationToken ct) =>
            {
                var items = await access.GetLinkedWorkItemsAsync(ct);
                return JsonSerializer.Serialize(items.Select(i => new
                {
                    id = i.Id,
                    title = i.Title,
                    url = i.Url
                }));
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_linked_work_items"),
                Description = "Returns a JSON array of work items linked to this pull request."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string body, string? filePath, int? lineNumber, CancellationToken ct) =>
            {
                await access.PostCommentAsync(body, filePath, lineNumber, ct);
                return "Comment posted.";
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "post_comment"),
                Description = "Posts a review comment on this pull request."
            }));

        return options;
    }
}
