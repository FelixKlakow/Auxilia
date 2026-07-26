using Auxilia.Workflows.Views;

namespace Auxilia.Workflows.AiAgent.CodingAgent;

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

public sealed record CodingAgentRequest(
    string Instruction, string WorkspaceDirectory, IAgentInteraction? Interaction = null);

/// <summary>
/// The two-way seam between a running agent and its operator, provided by the workflow: the agent
/// implementation raises questions through it (surfaced in the steering client as steering forms) and reads
/// guidance the operator sends into the live run. Null interaction = fully autonomous session.
/// </summary>
public interface IAgentInteraction
{
    /// <summary>Asks the operator and blocks until the question is answered.</summary>
    Task<AgentAnswer> AskAsync(AgentQuestion question, CancellationToken cancellationToken);

    /// <summary>Waits for the next guidance text the operator sends; blocks until one arrives.</summary>
    Task<string> WaitForGuidanceAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A question the agent asks the operator. Empty <see cref="Options"/> = pure free text;
/// <see cref="MultiSelect"/> selects radio vs. checkboxes; <see cref="AllowFreeText"/> adds a
/// free-text field beside the options.
/// </summary>
public sealed record AgentQuestion(
    string Prompt,
    IReadOnlyList<AgentQuestionOption> Options,
    bool MultiSelect = false,
    bool AllowFreeText = false);

public sealed record AgentQuestionOption(string Id, string Label, string? Description = null);

/// <summary>The operator's answer: the selected option ids and/or the free text.</summary>
public sealed record AgentAnswer(IReadOnlyList<string> SelectedIds, string? FreeText = null);

public sealed record CodingAgentResult(
    bool Success,
    string? Summary,
    int TurnCount = 0,
    decimal? TotalCostUsd = null,
    long? DurationMs = null,
    string? ErrorMessage = null);
