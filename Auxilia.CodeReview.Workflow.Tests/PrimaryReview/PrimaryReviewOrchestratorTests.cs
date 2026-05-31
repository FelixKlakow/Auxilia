using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Mcp;
using Auxilia.CodeReview.Workflow.PrimaryReview;
using Auxilia.CodeReview.Workflow.Verdicts;
using Auxilia.Workflows.AiAgent;
using Microsoft.Extensions.Options;
using Auxilia.CodeReview.Workflow.Compaction;
using Auxilia.CodeReview.Workflow.Context;

namespace Auxilia.CodeReview.Workflow.Tests.PrimaryReview;

[TestFixture]
public sealed class PrimaryReviewOrchestratorTests
{
    private static ReviewContext BuildContext(params ReviewableFile[] files) =>
        new()
        {
            PullRequest = new PullRequestReference("PR-1", "main", "feature"),
            RepositoryWorkingPath = "/repo",
            Files = files,
            LinkedWorkItems = []
        };

    private static ReviewableFile MakeFile(string path, FileCriticality criticality = FileCriticality.Normal) =>
        new()
        {
            FilePath = path,
            ChangeKind = Auxilia.Workflows.PullRequestAccess.ChangeKind.Modified,
            Criticality = criticality,
            Hunks = [new Auxilia.Workflows.PullRequestAccess.DiffHunk(path, 1, 5, 1, 5, "@@ -1 +1 @@\n-old\n+new")]
        };

    private static PrimaryReviewOrchestrator BuildOrchestrator(
        IAiAgent aiAgent,
        IStagedFindingsStore? store = null,
        VerdictMap? verdictMap = null,
        ContextCompactionOptions? opts = null)
    {
        store ??= new StagedFindingsStore();
        verdictMap ??= new VerdictMap();
        var options = Options.Create(opts ?? new ContextCompactionOptions { TokenLimitThreshold = 1_000_000 });
        return new PrimaryReviewOrchestrator(aiAgent, store, new ContextCompactionService(options), verdictMap);
    }

    [Test]
    public async Task RunAsync_AllFilesReceiveVerdict()
    {
        var verdictMap = new VerdictMap();
        var orchestrator = BuildOrchestrator(
            new FakeAiAgent(FileVerdict.Reviewed),
            verdictMap: verdictMap);

        var context = BuildContext(MakeFile("a.cs"), MakeFile("b.cs"), MakeFile("c.cs"));
        await orchestrator.RunAsync(context);

        Assert.That(verdictMap.AsReadOnly().Count, Is.EqualTo(3));
    }

    [Test]
    public async Task RunAsync_SkippedCriticalFile_RecordsFailed()
    {
        var verdictMap = new VerdictMap();
        var orchestrator = BuildOrchestrator(
            new FakeAiAgent(FileVerdict.Skipped),
            verdictMap: verdictMap);

        var context = BuildContext(MakeFile("critical.cs", FileCriticality.Critical));
        await orchestrator.RunAsync(context);

        Assert.That(verdictMap.AsReadOnly()["critical.cs"].Verdict, Is.EqualTo(FileVerdict.Failed));
    }

    [Test]
    public async Task RunAsync_SkippedNormalFile_StaysSkipped()
    {
        var verdictMap = new VerdictMap();
        var orchestrator = BuildOrchestrator(
            new FakeAiAgent(FileVerdict.Skipped),
            verdictMap: verdictMap);

        var context = BuildContext(MakeFile("normal.cs", FileCriticality.Normal));
        await orchestrator.RunAsync(context);

        Assert.That(verdictMap.AsReadOnly()["normal.cs"].Verdict, Is.EqualTo(FileVerdict.Skipped));
    }

    [Test]
    public async Task RunAsync_FindingsStaged_StoreNotEmpty()
    {
        var store = new StagedFindingsStore();
        var orchestrator = BuildOrchestrator(
            new FakeAiAgent(FileVerdict.Reviewed, oneFinding: true),
            store: store);

        var context = BuildContext(MakeFile("foo.cs"));
        await orchestrator.RunAsync(context);

        Assert.That(store.Snapshot(), Is.Not.Empty);
    }

    [Test]
    public async Task RunAsync_CompactionMidLoop_AllFilesStillReceiveVerdicts()
    {
        // Set threshold so compaction fires after ~2 files (estimate 1 token per char, ~100 chars per hunk → ~25 tokens)
        // Use a very low threshold to trigger compaction after file 2
        var verdictMap = new VerdictMap();
        var compactionOpts = new ContextCompactionOptions
        {
            TokenLimitThreshold = 10,   // 10 tokens
            CompactionTriggerFraction = 1.0  // trigger at 100% of limit
        };
        var agent = new FakeAiAgent(FileVerdict.Reviewed);
        var orchestrator = BuildOrchestrator(agent, verdictMap: verdictMap, opts: compactionOpts);

        var context = BuildContext(
            MakeFile("a.cs"), MakeFile("b.cs"), MakeFile("c.cs"), MakeFile("d.cs"), MakeFile("e.cs"));
        await orchestrator.RunAsync(context);

        Assert.That(verdictMap.AsReadOnly().Count, Is.EqualTo(5));
        Assert.That(agent.OpenSessionCallCount, Is.EqualTo(1),
            "Blueprint/37 in-session model: only one session is opened per RunAsync call");
    }

    [Test]
    public async Task RunAsync_NoToolCall_DefaultsToReviewedWithNoFindings()
    {
        var store = new StagedFindingsStore();
        var verdictMap = new VerdictMap();
        var orchestrator = BuildOrchestrator(
            new FakeAiAgent(FileVerdict.Reviewed, skipSink: true),
            store: store,
            verdictMap: verdictMap);

        var context = BuildContext(MakeFile("a.cs"), MakeFile("b.cs"));
        await orchestrator.RunAsync(context);

        Assert.That(verdictMap.AsReadOnly().Count, Is.EqualTo(2));
        Assert.That(verdictMap.AsReadOnly().Values.All(v => v.Verdict == FileVerdict.Reviewed), Is.True,
            "Absent tool calls default to FileVerdict.Reviewed");
        Assert.That(store.Snapshot(), Is.Empty,
            "No findings should be staged when tool call is absent");
    }

    // ---- Fake helpers ----

    private sealed class FakeAiAgent(FileVerdict verdict, bool oneFinding = false, bool skipSink = false) : IAiAgent
    {
        public int OpenSessionCallCount { get; private set; }

        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
        {
            OpenSessionCallCount++;
            return Task.FromResult<IAiSession>(new FakeAiSession(verdict, oneFinding, options, skipSink));
        }
    }

    private sealed class FakeAiSession(FileVerdict verdict, bool oneFinding, AiSessionOptions? options, bool skipSink = false) : IAiSession
    {
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            if (!skipSink)
            {
                var sink = options?.CapabilityTools?.OfType<CodeReviewResultSinkMcpTools>().FirstOrDefault();
                if (sink is not null)
                {
                    if (oneFinding)
                        sink.RecordFinding("finding.cs", 1, 2, FindingSeverity.Info, "Style", "test", null);
                    sink.RecordFileVerdict(verdict);
                }
            }
            return Task.FromResult(string.Empty);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task CompactAsync(string focusDescription, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
