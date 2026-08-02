using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Context;

public sealed record ReviewableFile
{
    public required string FilePath { get; init; }
    public required ChangeKind ChangeKind { get; init; }
    public required FileCriticality Criticality { get; init; }
    public required IReadOnlyList<DiffHunk> Hunks { get; init; }
}
