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
                // The production JIT activation path registers handlers only for DECLARED
                // slots, and pull-request-code-review declares no "workflow-bootstrap" slot —
                // in a container the dedicated case below never runs. Piggy-back the workflow
                // configuration on the always-declared repository slot so containerized runs
                // get the same TwoEyes/WriteBack setup as the eager/unit-test path.
                RegisterWorkflowBootstrap(services);
                break;

            case "pull-request":
                services.AddScoped<IPullRequestAccess>(_ => new FakePullRequestAccess());
                break;

            case "work-items":
                services.AddScoped<IWorkItemAccess>(_ => new FakeTaskSourceAccess());
                break;

            case "primary-reviewer":
                services.AddKeyedScoped<IAiAgent>(slotName, (sp, _) =>
                    new PrimaryReviewerAgent(AgentChatPublisher.Create(sp)));
                break;

            case "secondary-reviewer":
                services.AddKeyedScoped<IAiAgent>(slotName, (_, _) => new SecondaryReviewerAgent());
                break;

            case "workflow-bootstrap":
                RegisterWorkflowBootstrap(services);
                break;

            default:
                throw new InvalidOperationException($"Unknown slot: {slotName}");
        }
    }

    private static void RegisterWorkflowBootstrap(IServiceCollection services)
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"fake-cr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        services.AddScoped(_ => new TwoEyesConfiguration { Enabled = true });
        services.AddScoped(_ => new WriteBackConfiguration
        {
            MinimumSeverity = FindingSeverity.Info,
            // Summaries reach the work-items slot only when the run carries a linked
            // work item (see FakePullRequestAccess) — a no-op for plain runs.
            PostSummaryToWorkItems = true
        });
        services.AddCodeReviewWorkflow(outputDirectory);
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

        // When the run was triggered by a work-item event, the dispatcher forwards the
        // triggering item via WORKFLOW_CONTEXT__WORKITEMID; surfacing it as the PR's linked
        // work item lets end-to-end tests exercise the work-item write-back path. Runs
        // without that context behave as before (no linked items).
        public Task<IReadOnlyList<WorkItemReference>> GetLinkedWorkItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkItemReference>>(
                Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__WORKITEMID") is { Length: > 0 } workItemId
                    ? [new WorkItemReference(workItemId, null, null)]
                    : []);

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

    private sealed class PrimaryReviewerAgent(AgentChatPublisher chat) : IAiAgent
    {
        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IAiSession>(new PrimaryReviewerSession(options, chat));
    }

    private sealed class PrimaryReviewerSession(AiSessionOptions? options, AgentChatPublisher chat) : IAiSession
    {
        public async Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            // Demo conversation for the dashboard's "agent-chat" renderer: a real agent
            // provider would publish its actual session turns the same way.
            await chat.PublishUserAsync(
                "Review the changes in **src/Widget.cs** of fake-pr-1 and report findings via the result sink.",
                cancellationToken);
            await chat.PublishAssistantAsync(
                "Looking at the diff of `src/Widget.cs` now — the change modifies lines 1-10. " +
                "Let me read the surrounding file content to judge the style impact.",
                ct: cancellationToken);
            await chat.PublishToolAsync(
                "read_file", "Success", "// fake content", "src/Widget.cs", cancellationToken);
            await chat.PublishAssistantAsync(
                "Done. I found one **medium style finding** on lines 1-2 and recorded it through the " +
                "result sink; the file verdict is *Reviewed*.",
                ct: cancellationToken);

            var sink = options?.CapabilityTools?.OfType<CodeReviewResultSinkMcpTools>().FirstOrDefault();
            if (sink is not null)
            {
                sink.RecordFinding("src/Widget.cs", 1, 2, FindingSeverity.Medium, "Style", "Review finding", "Fix it");
                sink.RecordFileVerdict(FileVerdict.Reviewed);
            }
            return "";
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
