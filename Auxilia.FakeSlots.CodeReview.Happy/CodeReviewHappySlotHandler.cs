using Auxilia.CodeReview.Workflow;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Mcp;
using Auxilia.CodeReview.Workflow.Verdicts;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;
using Microsoft.Extensions.DependencyInjection;
using PrChangedFile = Auxilia.Workflows.PullRequestAccess.ChangedFile;
using ScChangedFile = Auxilia.Workflows.SourceControl.ChangedFile;

namespace Auxilia.FakeSlots.CodeReview.Happy;

public sealed class CodeReviewHappySlotHandler : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        switch (slotName)
        {
            case "repository":
                services.AddScoped<ISourceControlAccess>(_ => new FakeSourceControlAccess());
                break;

            case "pull-request":
                services.AddScoped<IPullRequestAccess>(_ => new FakePullRequestAccess());
                break;

            case "work-items":
                services.AddScoped<IWorkItemAccess>(_ => new FakeTaskSourceAccess());
                break;

            case "primary-reviewer":
                services.AddKeyedScoped<IAiAgent>(slotName, (_, _) => new PrimaryReviewerAgent());
                break;

            case "secondary-reviewer":
                services.AddKeyedScoped<IAiAgent>(slotName, (_, _) => new SecondaryReviewerAgent());
                break;

            case "workflow-bootstrap":
                var outputDirectory = Path.Combine(Path.GetTempPath(), $"fake-cr-{Guid.NewGuid():N}");
                Directory.CreateDirectory(outputDirectory);
                services.AddScoped(_ => new TwoEyesConfiguration { Enabled = true });
                services.AddScoped(_ => new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });
                services.AddCodeReviewWorkflow(outputDirectory);
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }

    private sealed class FakeSourceControlAccess : ISourceControlAccess
    {
        public string WorkingPath => "/fake/repo";

        public Task<IReadOnlyList<string>> ListFilesAsync(string? relativePath = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(["src/Widget.cs"]);

        public Task<string> ReadFileContentAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.FromResult("// fake content");

        public Task<IReadOnlyList<ScChangedFile>> GetChangedFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ScChangedFile>>([new ScChangedFile("src/Widget.cs", Auxilia.Workflows.SourceControl.ChangeKind.Modified)]);
    }

    private sealed class FakePullRequestAccess : IPullRequestAccess
    {
        public string PrIdentifier => "fake-pr-1";
        public string BaseRef => "main";
        public string HeadRef => "feature/fake";

        public Task<IReadOnlyList<PrChangedFile>> GetChangedFilesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PrChangedFile>>([new PrChangedFile("src/Widget.cs", Auxilia.Workflows.PullRequestAccess.ChangeKind.Modified)]);

        public Task<IReadOnlyList<DiffHunk>> GetDiffHunksAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DiffHunk>>([new DiffHunk(filePath, 1, 10, 1, 10, "@@ -1,10 +1,10 @@\n fake diff content")]);

        public Task<IReadOnlyList<ReviewComment>> GetCommentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReviewComment>>([]);

        public Task<IReadOnlyList<WorkItemReference>> GetLinkedWorkItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkItemReference>>([]);

        public Task PostCommentAsync(string body, string? filePath = null, int? lineNumber = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string> OpenPullRequestAsync(PullRequestOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult("fake-pr-1");
    }

    private sealed class FakeTaskSourceAccess : IWorkItemAccess
    {
        public Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkItem?>(null);

        public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkItem>>([]);

        public Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class PrimaryReviewerAgent : IAiAgent
    {
        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IAiSession>(new PrimaryReviewerSession(options));
    }

    private sealed class PrimaryReviewerSession(AiSessionOptions? options) : IAiSession
    {
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var sink = options?.CapabilityTools?.OfType<CodeReviewResultSinkMcpTools>().FirstOrDefault();
            if (sink is not null)
            {
                sink.RecordFinding("src/Widget.cs", 1, 2, FindingSeverity.Medium, "Style", "Review finding", "Fix it");
                sink.RecordFileVerdict(FileVerdict.Reviewed);
            }
            return Task.FromResult("");
        }

        public Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SecondaryReviewerAgent : IAiAgent
    {
        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IAiSession>(new SecondaryReviewerSession(options));
    }

    private sealed class SecondaryReviewerSession(AiSessionOptions? options) : IAiSession
    {
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var sink = options?.CapabilityTools?.OfType<CodeReviewResultSinkMcpTools>().FirstOrDefault();
            sink?.RecordSecondaryVerdict(SecondaryVerdict.Approved);
            return Task.FromResult("");
        }

        public Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
