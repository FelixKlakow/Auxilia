using Auxilia.Workflows.Policy;

namespace Auxilia.Workflows.PullRequestAccess;

/// <summary>
/// Policy-guarded decorator for <see cref="IPullRequestAccess"/>.
/// Every <c>*Async</c> method checks <see cref="IToolPolicy.IsAllowed"/> before delegating.
/// </summary>
public sealed class PolicyGuardedPullRequestAccess : IPullRequestAccess
{
    private readonly IPullRequestAccess _inner;
    private readonly IToolPolicy _policy;
    private readonly string _slotName;

    public PolicyGuardedPullRequestAccess(IPullRequestAccess inner, IToolPolicy policy, string slotName)
    {
        _inner = inner;
        _policy = policy;
        _slotName = slotName;
    }

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("pull_request.get_changed_files"))
            throw new ToolPolicyDeniedException("pull_request.get_changed_files", _slotName);
        return _inner.GetChangedFilesAsync(cancellationToken);
    }

    public Task<IReadOnlyList<DiffHunk>> GetDiffHunksAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("pull_request.get_diff_hunks"))
            throw new ToolPolicyDeniedException("pull_request.get_diff_hunks", _slotName);
        return _inner.GetDiffHunksAsync(filePath, cancellationToken);
    }

    public Task<IReadOnlyList<ReviewComment>> GetCommentsAsync(CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("pull_request.get_comments"))
            throw new ToolPolicyDeniedException("pull_request.get_comments", _slotName);
        return _inner.GetCommentsAsync(cancellationToken);
    }

    public Task<IReadOnlyList<WorkItemReference>> GetLinkedWorkItemsAsync(CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("pull_request.get_linked_work_items"))
            throw new ToolPolicyDeniedException("pull_request.get_linked_work_items", _slotName);
        return _inner.GetLinkedWorkItemsAsync(cancellationToken);
    }

    public Task PostCommentAsync(string body, string? filePath = null, int? lineNumber = null, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("pull_request.post_comment"))
            throw new ToolPolicyDeniedException("pull_request.post_comment", _slotName);
        return _inner.PostCommentAsync(body, filePath, lineNumber, cancellationToken);
    }

    public Task<string> OpenPullRequestAsync(PullRequestOptions options, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("pull_request.open_pull_request"))
            throw new ToolPolicyDeniedException("pull_request.open_pull_request", _slotName);
        return _inner.OpenPullRequestAsync(options, cancellationToken);
    }
}
