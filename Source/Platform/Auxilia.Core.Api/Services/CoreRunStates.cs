using System.Text.Json;
using Auxilia.Core.Contracts;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Core.Api.Services;

/// <summary>Lifecycle-state helpers shared across the Core run surface.</summary>
public static class CoreRunStates
{
    public static bool IsTerminal(string? state) => RunStates.IsTerminal(state);

    /// <summary>True when the serialized <see cref="WorkflowStatusEvent"/> is a terminal transition.</summary>
    public static bool IsTerminalStatus(string statusPayloadJson)
    {
        try
        {
            return IsTerminal(
                JsonSerializer.Deserialize<WorkflowStatusEvent>(statusPayloadJson, JsonSerializerOptions.Web)?.State);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
