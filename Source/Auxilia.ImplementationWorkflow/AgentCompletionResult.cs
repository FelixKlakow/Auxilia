namespace Auxilia.ImplementationWorkflow;

public sealed record AgentCompletionResult(
    string AgentResponseText,
    string BranchName,
    string WorkItemId);
