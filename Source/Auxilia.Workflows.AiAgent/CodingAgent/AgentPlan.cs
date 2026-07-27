namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>One point of the agent's current plan.</summary>
public sealed record AgentPlanItem(string Content, string Status);

/// <summary>Status vocabulary of plan items (Claude's TodoWrite wording, shared by all agents).</summary>
public static class AgentPlanStatuses
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
}

/// <summary>
/// A full snapshot of the agent's plan — published on the <see cref="ViewName"/> view whenever
/// the agent revises it; observers always render the LATEST snapshot, never a diff.
/// </summary>
public sealed record AgentPlanUpdate(IReadOnlyList<AgentPlanItem> Items)
{
    public const string ViewName = "plan";
}
