namespace Auxilia.Workflows.PullRequestAccess;

public sealed record PullRequestOptions(
    string Title,
    string SourceBranch,
    string TargetBranch,
    string? Description = null,
    IReadOnlyList<string>? LinkedWorkItemIds = null);
