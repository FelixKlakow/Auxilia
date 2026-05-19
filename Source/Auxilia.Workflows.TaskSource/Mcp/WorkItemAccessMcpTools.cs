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
        : base(slotName, BuildOptions(slotName, access), loggerFactory)
    {
    }

    private static McpServerOptions BuildOptions(string slotName, IWorkItemAccess access)
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
            async (string id, CancellationToken ct) =>
            {
                var item = await access.GetWorkItemAsync(id, ct);
                if (item is null)
                    return $"Work item '{id}' not found.";
                return $"Id: {item.Id}\nTitle: {item.Title}\nStatus: {item.Status}\nAssignee: {item.AssigneeDisplayName}\nDescription: {item.Description}\nLabels: {string.Join(", ", item.Labels)}";
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_work_item"),
                Description = "Returns a plain-text summary of a single work item by ID."
            }));

        options.ToolCollection.Add(McpServerTool.Create(
            async (string idsJson, CancellationToken ct) =>
            {
                var ids = JsonSerializer.Deserialize<IEnumerable<string>>(idsJson) ?? [];
                var items = await access.GetWorkItemsAsync(ids, ct);
                return JsonSerializer.Serialize(items.Select(i => new
                {
                    id = i.Id,
                    title = i.Title,
                    status = i.Status,
                    assignee = i.AssigneeDisplayName,
                    description = i.Description,
                    labels = i.Labels
                }));
            },
            new McpServerToolCreateOptions
            {
                Name = SlotMcpPrefix.Format(slotName, "get_work_items"),
                Description = "Returns a JSON array of work items for the given JSON-encoded array of IDs."
            }));

        return options;
    }
}
