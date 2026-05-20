using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.Policy;

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
        : base(slotName, BuildOptions(slotName, access, loggerFactory), loggerFactory)
    {
    }

    internal static McpServerOptions BuildOptions(string slotName, IPullRequestAccess access, ILoggerFactory? loggerFactory)
    {
        var logger = loggerFactory?.CreateLogger<PullRequestAccessMcpTools>();
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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
                try
                {
                    var files = await access.GetChangedFilesAsync(ct);
                    return JsonSerializer.Serialize(
                        files.Select(f => new { filePath = f.RelativePath, kind = f.Kind.ToString() }),
                        jsonOptions);
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
                Description = "Returns a JSON array of files changed in this pull request."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string filePath, CancellationToken ct) =>
            {
                try
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
                    }), jsonOptions);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in get_diff_hunks");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_diff_hunks"),
                Description = "Returns a JSON array of diff hunks for the specified file."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (CancellationToken ct) =>
            {
                try
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
                    }), jsonOptions);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in get_comments");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_comments"),
                Description = "Returns a JSON array of review comments on this pull request."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (CancellationToken ct) =>
            {
                try
                {
                    var items = await access.GetLinkedWorkItemsAsync(ct);
                    return JsonSerializer.Serialize(items.Select(i => new
                    {
                        id = i.Id,
                        title = i.Title,
                        url = i.Url
                    }), jsonOptions);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in get_linked_work_items");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_linked_work_items"),
                Description = "Returns a JSON array of work items linked to this pull request."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string body, string? filePath, int? lineNumber, CancellationToken ct) =>
            {
                try
                {
                    await access.PostCommentAsync(body, filePath, lineNumber, ct);
                    return "Comment posted.";
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in post_comment");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "post_comment"),
                Description = "Posts a review comment on this pull request."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string optionsJson, CancellationToken ct) =>
            {
                try
                {
                    var prOptions = JsonSerializer.Deserialize<PullRequestOptions>(optionsJson, jsonOptions)
                        ?? throw new ArgumentException("Failed to deserialize PullRequestOptions.");
                    var url = await access.OpenPullRequestAsync(prOptions, ct);
                    return $"Pull request opened: {url}";
                }
                catch (ToolPolicyDeniedException ex)
                {
                    return $"Operation denied by tool policy: {ex.Key}";
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in open_pull_request");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "open_pull_request"),
                Description = "Opens a new pull request. Accepts a JSON object with title, sourceBranch, targetBranch, and optional description and linkedWorkItemIds."
            }));

        return options;
    }
}
