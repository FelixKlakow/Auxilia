namespace Auxilia.Workflows.PullRequestAccess;

/// <summary>Behavioral contract for pull-request host access inside a workflow.</summary>
public interface IPullRequestAccess
{
    Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiffHunk>> GetDiffHunksAsync(string filePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReviewComment>> GetCommentsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkItemReference>> GetLinkedWorkItemsAsync(CancellationToken cancellationToken = default);

    Task PostCommentAsync(string body, string? filePath = null, int? lineNumber = null, CancellationToken cancellationToken = default);
}
