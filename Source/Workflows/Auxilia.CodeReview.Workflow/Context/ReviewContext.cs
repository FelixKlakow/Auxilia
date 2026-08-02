namespace Auxilia.CodeReview.Workflow.Context;

public sealed record ReviewContext
{
    public required PullRequestReference PullRequest { get; init; }
    public required string RepositoryWorkingPath { get; init; }
    public required IReadOnlyList<ReviewableFile> Files { get; init; }
    public required IReadOnlyList<WorkItemSummary> LinkedWorkItems { get; init; }
}
