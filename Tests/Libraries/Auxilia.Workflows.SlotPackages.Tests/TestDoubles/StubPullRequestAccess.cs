using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.Workflows.SlotPackages.Tests.TestDoubles;

public sealed class StubPullRequestAccess : IPullRequestAccess
{
    public string PrIdentifier => "PR-1";
    public string BaseRef => "main";
    public string HeadRef => "feature/stub";

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ChangedFile>>([
            new ChangedFile("src/Program.cs", ChangeKind.Modified),
            new ChangedFile("docs/README.md", ChangeKind.Added)
        ]);

    public Task<IReadOnlyList<DiffHunk>> GetDiffHunksAsync(string filePath, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DiffHunk>>([
            new DiffHunk(filePath, 1, 3, 1, 5, "@@ -1,3 +1,5 @@\n-old line\n+new line")
        ]);

    public Task<IReadOnlyList<ReviewComment>> GetCommentsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ReviewComment>>([
            new ReviewComment("comment-1", "Looks good!", "reviewer", null, null, DateTimeOffset.UtcNow)
        ]);

    public Task<IReadOnlyList<WorkItemReference>> GetLinkedWorkItemsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<WorkItemReference>>([
            new WorkItemReference("WI-42", "Fix the bug", "https://example.com/wi/42")
        ]);

    public Task PostCommentAsync(string body, string? filePath = null, int? lineNumber = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<string> OpenPullRequestAsync(PullRequestOptions options, CancellationToken cancellationToken = default)
        => Task.FromResult("https://example.com/pr/1");
}
