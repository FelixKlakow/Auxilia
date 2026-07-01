using Auxilia.Workflows.Views;

namespace Auxilia.ClaudeCode.Workflow;

/// <summary>
/// Slot contract of the "coding-agent" slot: runs an autonomous coding agent against the
/// run's workspace and streams every conversation turn while it works.
/// </summary>
public interface ICodingAgent
{
    Task<CodingAgentResult> RunAsync(
        CodingAgentRequest request,
        Func<AgentChatEntry, CancellationToken, Task> onChatEntry,
        CancellationToken cancellationToken = default);
}

public sealed record CodingAgentRequest(string Instruction, string WorkspaceDirectory);

public sealed record CodingAgentResult(
    bool Success,
    string? Summary,
    int TurnCount = 0,
    decimal? TotalCostUsd = null,
    long? DurationMs = null,
    string? ErrorMessage = null);
