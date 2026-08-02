namespace Auxilia.Workflows.PullRequestAccess;

/// <summary>Behavioral contract for pull-request host access inside a workflow.</summary>
public interface IPullRequestAccess
{
    /// <summary>Provider-neutral identifier for this pull request (e.g. a PR number or URL).</summary>
    string PrIdentifier { get; }

    /// <summary>The base branch this pull request targets (e.g. "main").</summary>
    string BaseRef { get; }

    /// <summary>The head branch containing the changes being reviewed.</summary>
    string HeadRef { get; }

    Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiffHunk>> GetDiffHunksAsync(string filePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReviewComment>> GetCommentsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkItemReference>> GetLinkedWorkItemsAsync(CancellationToken cancellationToken = default);

    Task PostCommentAsync(string body, string? filePath = null, int? lineNumber = null, CancellationToken cancellationToken = default);

    Task<string> OpenPullRequestAsync(PullRequestOptions options, CancellationToken cancellationToken = default);
}
