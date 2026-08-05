using Auxilia.Steering.Codec;
using Auxilia.Workflows.Views;

namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// Routes console-session events onto a run's surfaces — shared by every console-hosting
/// application: attention and turn boundaries ride the steering view (the steering client's
/// notification cue), tool activity becomes detail-tagged chat entries (default hidden,
/// per-tag toggle), and a turn's closing text becomes a real conversation entry.
/// </summary>
public sealed class ConsoleEventViews(IViewPublisher views, TimeProvider time)
{
    private int _turns;

    public async Task PublishAsync(ConsoleSessionEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case ConsoleSessionEvent.Attention:
                await views.PublishAsync(
                    Steering.OperatorChannel.ViewName, new SteeringAttention(evt.Message), ct);
                await PublishChatAsync(new AgentChatEntry(
                    AgentChatRole.System, evt.Message, time.GetUtcNow(),
                    Label: "Waiting for you"), ct);
                break;
            case ConsoleSessionEvent.TurnEnded:
                if (evt.Message.Length > 0)
                    await PublishChatAsync(new AgentChatEntry(
                        AgentChatRole.Assistant, evt.Message, time.GetUtcNow()), ct);
                await views.PublishAsync(
                    Steering.OperatorChannel.ViewName,
                    new SteeringTurnEnded(++_turns, Summarize(evt.Message)), ct);
                break;
            case ConsoleSessionEvent.PlanUpdated:
                if (Deserialize(evt.DetailJson) is { Count: > 0 } plan)
                    await views.PublishAsync(AgentPlanUpdate.ViewName, new AgentPlanUpdate(plan), ct);
                break;
            case ConsoleSessionEvent.ToolStarted or ConsoleSessionEvent.ToolFinished:
                await PublishChatAsync(new AgentChatEntry(
                    AgentChatRole.System, evt.Message, time.GetUtcNow(),
                    Label: evt.ToolName, DetailTag: "console-activity"), ct);
                break;
            default:
                await views.PublishAsync(
                    AgentSessionApplication.ProgressViewName,
                    new SessionProgressEntry("session", evt.Message, time.GetUtcNow()), ct);
                break;
        }
    }

    private Task PublishChatAsync(AgentChatEntry entry, CancellationToken ct)
        => views.PublishAsync(AgentSessionApplication.ChatViewName, entry, ct);

    private static string? Summarize(string message)
        => message.Length == 0 ? null : message.Length <= 280 ? message : message[..280] + "…";

    private static IReadOnlyList<AgentPlanItem>? Deserialize(string? planJson)
    {
        if (planJson is not { Length: > 0 })
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<AgentPlanItem>>(planJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

}
