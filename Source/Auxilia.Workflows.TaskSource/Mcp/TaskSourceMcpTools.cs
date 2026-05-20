using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.Policy;

namespace Auxilia.Workflows.TaskSource.Mcp;

/// <summary>
/// MCP tool server for <see cref="ITaskSourceAccess"/>.
/// Exposes task-source operations as prefixed MCP tools under the given slot name.
/// </summary>
public sealed class TaskSourceMcpTools : CapabilityMcpToolsBase
{
    public TaskSourceMcpTools(
        string slotName,
        ITaskSourceAccess access,
        ILoggerFactory? loggerFactory = null)
        : base(slotName, BuildOptions(slotName, access, loggerFactory), loggerFactory)
    {
    }

    internal static McpServerOptions BuildOptions(
        string slotName,
        ITaskSourceAccess access,
        ILoggerFactory? loggerFactory)
    {
        var logger = loggerFactory?.CreateLogger<TaskSourceMcpTools>();
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
            async (string id, CancellationToken ct) =>
            {
                try
                {
                    var item = await access.GetWorkItemAsync(id, ct);
                    if (item is null)
                        return "Not found.";
                    return $"Id: {item.Id}\nTitle: {item.Title}\nStatus: {item.Status ?? "N/A"}\nAssignee: {item.AssigneeDisplayName ?? "N/A"}\nDescription: {item.Description ?? "N/A"}\nLabels: {string.Join(", ", item.Labels)}";
                }
                catch (ToolPolicyDeniedException ex)
                {
                    return $"Operation denied by tool policy: {ex.Key}";
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in get_work_item");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_work_item"),
                Description = "Returns a plain-text summary of a single work item by ID."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string idsJson, CancellationToken ct) =>
            {
                try
                {
                    var ids = JsonSerializer.Deserialize<IEnumerable<string>>(idsJson) ?? [];
                    var items = await access.GetWorkItemsAsync(ids, ct);
                    return JsonSerializer.Serialize(items.Select(i => new
                    {
                        id = i.Id,
                        title = i.Title,
                        status = i.Status,
                        assigneeDisplayName = i.AssigneeDisplayName,
                        description = i.Description,
                        labels = i.Labels
                    }), jsonOptions);
                }
                catch (ToolPolicyDeniedException ex)
                {
                    return $"Operation denied by tool policy: {ex.Key}";
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in get_work_items");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_work_items"),
                Description = "Returns a JSON array of work items for the given JSON-encoded array of IDs."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string id, string newStatus, CancellationToken ct) =>
            {
                try
                {
                    await access.UpdateStatusAsync(id, newStatus, ct);
                    return $"Status of work item '{id}' updated to '{newStatus}'.";
                }
                catch (ToolPolicyDeniedException ex)
                {
                    return $"Operation denied by tool policy: {ex.Key}";
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in update_status");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "update_status"),
                Description = "Updates the status of a work item."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string id, string comment, CancellationToken ct) =>
            {
                try
                {
                    await access.PostCommentAsync(id, comment, ct);
                    return $"Comment posted on work item '{id}'.";
                }
                catch (ToolPolicyDeniedException ex)
                {
                    return $"Operation denied by tool policy: {ex.Key}";
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
                Description = "Posts a comment on a work item."
            }));

        return options;
    }
}
