namespace Auxilia.ImplementationWorkflow.Signals;

public sealed record FailedSignalPayload
{
    public required string FailureReason { get; init; }
    public required string WorkItemId { get; init; }
    public string? PartialBranchName { get; init; }
}
