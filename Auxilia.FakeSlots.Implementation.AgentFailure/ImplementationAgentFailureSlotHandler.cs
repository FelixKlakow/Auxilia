using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.TaskSource;
using Auxilia.Workflows.TestRunner;
using Microsoft.Extensions.DependencyInjection;
using PrChangedFile = Auxilia.Workflows.PullRequestAccess.ChangedFile;
using ScChangedFile = Auxilia.Workflows.SourceControl.ChangedFile;

namespace Auxilia.FakeSlots.Implementation.AgentFailure;

public sealed class ImplementationAgentFailureSlotHandler : ISlotHandler
{
    public void Register(IServiceCollection services, string slotName, Type serviceType, SlotConfiguration configuration)
    {
        switch (slotName)
        {
            case "repository":
                var repoAccess = new FakeSourceControlWriteAccess();
                services.AddKeyedScoped<ISourceControlWriteAccess>(slotName, (_, _) => repoAccess);
                services.AddKeyedScoped<ISourceControlAccess>(slotName, (_, _) => repoAccess);
                break;

            case "task-source":
                services.AddKeyedScoped<ITaskSourceAccess>(slotName, (_, _) => new FakeTaskSourceAccess());
                break;

            case "implementation-agent":
                services.AddKeyedScoped<IAiAgent>(slotName, (_, _) => new FailingImplementationAgent());
                break;

            case "reviewer-agent":
                services.AddKeyedScoped<IAiAgent>(slotName, (_, _) => new ReviewerAgent());
                break;

            case "test-runner":
                services.AddKeyedScoped<ITestRunner>(slotName, (_, _) => new FakeTestRunner());
                break;

            case "pull-request":
                services.AddKeyedScoped<IPullRequestAccess>(slotName, (_, _) => new FakePullRequestAccess());
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }

    private sealed class FakeSourceControlWriteAccess : ISourceControlWriteAccess
    {
        public string WorkingPath => "/fake/repo";

        public Task<IReadOnlyList<string>> ListFilesAsync(string? relativePath = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string> ReadFileContentAsync(string relativePath, CancellationToken cancellationToken = default)
            => Task.FromResult("// fake content");

        public Task<IReadOnlyList<ScChangedFile>> GetChangedFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ScChangedFile>>([]);

        public Task CreateBranchAsync(string branchName, string? fromRef = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task WriteFileAsync(string relativePath, string content, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CommitAsync(string message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PushAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeTaskSourceAccess : ITaskSourceAccess
    {
        public Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkItem?>(new WorkItem("WI-1", "Implement feature", null, null, null, []));

        public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkItem>>([new WorkItem("WI-1", "Implement feature", null, null, null, [])]);

        public Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task UpdateStatusAsync(string id, string newStatus, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FailingImplementationAgent : IAiAgent
    {
        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new Exception("Simulated agent failure");
    }

    private sealed class ReviewerAgent : IAiAgent
    {
        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IAiSession>(new ReviewerSession());
    }

    private sealed class ReviewerSession : IAiSession
    {
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult("");

        public Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTestRunner : ITestRunner
    {
        public Task<TestRunResult> RunTestsAsync(TestRunRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new TestRunResult(Passed: true, ExitCode: 0, PassCount: 1, FailCount: 0, SkippedCount: 0, LogOutput: "All tests passed."));
    }

    private sealed class FakePullRequestAccess : IPullRequestAccess
    {
        public string PrIdentifier => "fake-pr-1";
        public string BaseRef => "main";
        public string HeadRef => "feature/fake";

        public Task<IReadOnlyList<PrChangedFile>> GetChangedFilesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PrChangedFile>>([]);

        public Task<IReadOnlyList<DiffHunk>> GetDiffHunksAsync(string filePath, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DiffHunk>>([]);

        public Task<IReadOnlyList<ReviewComment>> GetCommentsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReviewComment>>([]);

        public Task<IReadOnlyList<WorkItemReference>> GetLinkedWorkItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkItemReference>>([]);

        public Task PostCommentAsync(string body, string? filePath = null, int? lineNumber = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<string> OpenPullRequestAsync(PullRequestOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult("fake-pr-1");
    }
}
