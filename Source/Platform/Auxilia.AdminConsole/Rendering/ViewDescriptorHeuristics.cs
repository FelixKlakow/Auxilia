using System.Text.Json;
using Auxilia.Workflows.Views;

namespace Auxilia.AdminConsole.Rendering;

/// <summary>
/// There is no persisted view descriptor on the client surface, so pages synthesize one per view:
/// items shaped like an <see cref="AgentChatEntry"/> render through the agent-chat plug-in;
/// everything else falls back to the generic log rendering.
/// </summary>
public static class ViewDescriptorHeuristics
{
    public static ViewDescriptor DescriptorFor(string viewName, IReadOnlyList<ViewDataRecord> items)
        => LooksLikeAgentChat(items)
            ? new ViewDescriptor(viewName, "", ViewRendering.Custom, ViewLifecycle.Live, AgentChatEntry.RendererKey)
            : new ViewDescriptor(viewName, "", ViewRendering.Log, ViewLifecycle.Live);

    private static bool LooksLikeAgentChat(IReadOnlyList<ViewDataRecord> items)
    {
        if (items.Count == 0)
            return false;
        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(items[0].PayloadJson);
            return element is { ValueKind: JsonValueKind.Object }
                   && element.TryGetProperty("role", out var role)
                   && role.ValueKind == JsonValueKind.String
                   && Enum.TryParse<AgentChatRole>(role.GetString(), ignoreCase: true, out _)
                   && element.TryGetProperty("content", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
