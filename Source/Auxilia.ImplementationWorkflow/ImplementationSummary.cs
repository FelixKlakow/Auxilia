namespace Auxilia.ImplementationWorkflow;

public sealed record ImplementationSummary
{
    public required string WorkItemId { get; init; }
    public required string BranchName { get; init; }
    public required string PrUrl { get; init; }
    public required IReadOnlyList<ReviewNote> ReviewNotes { get; init; }
}
