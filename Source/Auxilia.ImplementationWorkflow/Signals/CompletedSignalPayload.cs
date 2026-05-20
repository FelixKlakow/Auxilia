namespace Auxilia.ImplementationWorkflow.Signals;

public sealed record CompletedSignalPayload
{
    public required string PrUrl { get; init; }
    public required string BranchName { get; init; }
    public required string WorkItemId { get; init; }
}
