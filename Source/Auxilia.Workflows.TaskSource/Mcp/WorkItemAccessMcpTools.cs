using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Auxilia.Workflows.Mcp;

namespace Auxilia.Workflows.TaskSource.Mcp;

/// <summary>
/// MCP tool server for <see cref="IWorkItemAccess"/>.
/// Exposes work-item access operations as prefixed MCP tools under the given slot name.
/// </summary>
public sealed class WorkItemAccessMcpTools : CapabilityMcpToolsBase
{
    public WorkItemAccessMcpTools(
        string slotName,
        IWorkItemAccess access,
        ILoggerFactory? loggerFactory = null)
        : base(slotName, BuildOptions(slotName, access, loggerFactory), loggerFactory)
    {
    }

    private static McpServerOptions BuildOptions(string slotName, IWorkItemAccess access, ILoggerFactory? loggerFactory)
    {
        var logger = loggerFactory?.CreateLogger<WorkItemAccessMcpTools>();
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
            async (string id, CancellationToken ct) =>
            {
                try
                {
                    var relations = await access.GetRelationsAsync(id, ct);
                    return JsonSerializer.Serialize(relations.Select(r => new
                    {
                        kind = r.Kind,
                        targetId = r.TargetId,
                        title = r.Title,
                        url = r.Url
                    }), jsonOptions);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in get_work_item_relations");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_work_item_relations"),
                Description = "Returns a work item's relations as JSON: parent/child/related "
                              + "work items (kind, targetId, title) and hyperlinks (kind 'link', url). "
                              + "Fetch a related item's full detail with get_work_item."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string id, CancellationToken ct) =>
            {
                try
                {
                    var states = await access.GetStatesAsync(id, ct);
                    return states.Count == 0
                        ? "This work item's source has no state model."
                        : string.Join(", ", states);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Error in get_work_item_states");
                    return $"Error: {ex.Message}";
                }
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_work_item_states"),
                Description = "Returns the state vocabulary of the work item's type at its source."
            }));

        return options;
    }
}
