using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.CodeReview.Workflow.Tests;

[TestFixture]
public sealed class WriteBackServiceTests
{
    private static ReviewFinding MakeFinding(FindingSeverity severity) =>
        new()
        {
            FilePath = "a.cs", LineStart = 1, LineEnd = 2,
            Severity = severity, Category = "Quality", Message = "msg",
            Suggestion = null, PrimaryReviewerAttribution = "primary",
            TwoEyesVerdict = SecondaryVerdict.NotReviewed, SecondaryReviewerAttribution = null
        };

    private static CodeReviewResult MakeResult(params ReviewFinding[] findings) =>
        new() { PrIdentifier = "PR-1", Findings = findings, Summary = "Review complete" };

    [Test]
    public async Task PostCommentAsync_CalledOncePerThresholdPassingFinding()
    {
        var prAccess = new FakePullRequestAccess();
        var svc = new WriteBackService(prAccess, new FakeWorkItemAccess(),
            new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Medium });

        // High (above Medium) and Critical (above Medium) should be written back
        var result = MakeResult(
            MakeFinding(FindingSeverity.Critical),
            MakeFinding(FindingSeverity.High),
            MakeFinding(FindingSeverity.Medium),
            MakeFinding(FindingSeverity.Low),
            MakeFinding(FindingSeverity.Info));

        await svc.WriteBackAsync(result, []);

        // Critical=0, High=1, Medium=2, Low=3, Info=4 — "at or above" means severity value <= threshold value
        // Critical, High, Medium are at or above Medium threshold → 3 comments
        Assert.That(prAccess.PostCommentCount, Is.EqualTo(3));
    }

    [Test]
    public async Task BelowThreshold_NotPostedToPs()
    {
        var prAccess = new FakePullRequestAccess();
        var svc = new WriteBackService(prAccess, new FakeWorkItemAccess(),
            new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Critical });

        var result = MakeResult(MakeFinding(FindingSeverity.Info));
        await svc.WriteBackAsync(result, []);

        Assert.That(prAccess.PostCommentCount, Is.EqualTo(0));
    }

    [Test]
    public async Task PostSummaryToWorkItems_False_WorkItemAccessNotCalled()
    {
        var workItemAccess = new FakeWorkItemAccess();
        var svc = new WriteBackService(new FakePullRequestAccess(), workItemAccess,
            new WriteBackConfiguration
            {
                MinimumSeverity = FindingSeverity.Info,
                PostSummaryToWorkItems = false
            });

        var result = MakeResult(MakeFinding(FindingSeverity.Info));
        await svc.WriteBackAsync(result, ["WI-1"]);

        Assert.That(workItemAccess.PostCommentCount, Is.EqualTo(0));
    }

    [Test]
    public async Task PostSummaryToWorkItems_True_WorkItemAccessCalledPerWorkItem()
    {
        var workItemAccess = new FakeWorkItemAccess();
        var svc = new WriteBackService(new FakePullRequestAccess(), workItemAccess,
            new WriteBackConfiguration
            {
                MinimumSeverity = FindingSeverity.Info,
                PostSummaryToWorkItems = true
            });

        var result = MakeResult(MakeFinding(FindingSeverity.Info));
        await svc.WriteBackAsync(result, ["WI-1", "WI-2"]);

        Assert.That(workItemAccess.PostCommentCount, Is.EqualTo(2));
    }

    // ---- Fakes ----

    private sealed class FakePullRequestAccess : IPullRequestAccess
    {
        public int PostCommentCount { get; private set; }

        public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ChangedFile>>([]);

        public Task<IReadOnlyList<DiffHunk>> GetDiffHunksAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DiffHunk>>([]);

        public Task<IReadOnlyList<ReviewComment>> GetCommentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReviewComment>>([]);

        public Task<IReadOnlyList<WorkItemReference>> GetLinkedWorkItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkItemReference>>([]);

        public Task PostCommentAsync(string body, string? filePath = null, int? lineNumber = null, CancellationToken cancellationToken = default)
        {
            PostCommentCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkItemAccess : IWorkItemAccess
    {
        public int PostCommentCount { get; private set; }

        public Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkItem?>(null);

        public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkItem>>([]);

        public Task PostCommentAsync(string workItemId, string comment, CancellationToken cancellationToken = default)
        {
            PostCommentCount++;
            return Task.CompletedTask;
        }
    }
}
