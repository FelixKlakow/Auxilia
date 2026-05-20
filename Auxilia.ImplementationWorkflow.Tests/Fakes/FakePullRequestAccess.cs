using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakePullRequestAccess : IPullRequestAccess
{
    public string PrUrl { get; set; } = "https://example.com/pr/1";
    public List<(string Title, PullRequestOptions Options)> OpenedPullRequests { get; } = new();

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChangedFile> result = [];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<DiffHunk>> GetDiffHunksAsync(string filePath, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DiffHunk> result = [];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ReviewComment>> GetCommentsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ReviewComment> result = [];
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<WorkItemReference>> GetLinkedWorkItemsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WorkItemReference> result = [];
        return Task.FromResult(result);
    }

    public Task PostCommentAsync(string body, string? filePath = null, int? lineNumber = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<string> OpenPullRequestAsync(PullRequestOptions options, CancellationToken cancellationToken = default)
    {
        OpenedPullRequests.Add((options.Title, options));
        return Task.FromResult(PrUrl);
    }
}
