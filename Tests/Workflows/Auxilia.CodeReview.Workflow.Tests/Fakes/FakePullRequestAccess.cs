using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakePullRequestAccess : IPullRequestAccess
{
    private readonly IReadOnlyList<ChangedFile> _changedFiles;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<DiffHunk>> _diffHunks;
    private readonly IReadOnlyList<ReviewComment> _comments;
    private readonly IReadOnlyList<WorkItemReference> _linkedWorkItems;
    private readonly Exception? _throwOnPost;

    public FakePullRequestAccess(
        IReadOnlyList<ChangedFile>? changedFiles = null,
        IReadOnlyDictionary<string, IReadOnlyList<DiffHunk>>? diffHunks = null,
        IReadOnlyList<ReviewComment>? comments = null,
        IReadOnlyList<WorkItemReference>? linkedWorkItems = null,
        Exception? throwOnPost = null,
        string prIdentifier = "PR-1",
        string baseRef = "main",
        string headRef = "feature/test")
    {
        _changedFiles = changedFiles ?? [];
        _diffHunks = diffHunks ?? new Dictionary<string, IReadOnlyList<DiffHunk>>();
        _comments = comments ?? [];
        _linkedWorkItems = linkedWorkItems ?? [];
        _throwOnPost = throwOnPost;
        PrIdentifier = prIdentifier;
        BaseRef = baseRef;
        HeadRef = headRef;
    }

    public string PrIdentifier { get; }
    public string BaseRef { get; }
    public string HeadRef { get; }

    public List<(string Body, string? FilePath, int? LineNumber)> PostedComments { get; } = [];

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_changedFiles);

    public Task<IReadOnlyList<DiffHunk>> GetDiffHunksAsync(string filePath, CancellationToken cancellationToken = default)
    {
        _diffHunks.TryGetValue(filePath, out var hunks);
        return Task.FromResult(hunks ?? (IReadOnlyList<DiffHunk>)[]);
    }

    public Task<IReadOnlyList<ReviewComment>> GetCommentsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_comments);

    public Task<IReadOnlyList<WorkItemReference>> GetLinkedWorkItemsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_linkedWorkItems);

    public Task PostCommentAsync(string body, string? filePath = null, int? lineNumber = null, CancellationToken cancellationToken = default)
    {
        if (_throwOnPost is not null)
            throw _throwOnPost;

        PostedComments.Add((body, filePath, lineNumber));
        return Task.CompletedTask;
    }

    public Task<string> OpenPullRequestAsync(PullRequestOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult("https://example.com/pr/1");
}
