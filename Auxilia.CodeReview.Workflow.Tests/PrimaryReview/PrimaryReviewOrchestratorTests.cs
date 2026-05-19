using Auxilia.CodeReview.Workflow.Findings;
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
            new FakeAiAgent("Reviewed"),
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
            new FakeAiAgent("Skipped"),
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
            new FakeAiAgent("Skipped"),
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
            new FakeAiAgent("Reviewed", oneFinding: true),
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
        var orchestrator = BuildOrchestrator(
            new FakeAiAgent("Reviewed"),
            verdictMap: verdictMap,
            opts: compactionOpts);

        var context = BuildContext(
            MakeFile("a.cs"), MakeFile("b.cs"), MakeFile("c.cs"), MakeFile("d.cs"), MakeFile("e.cs"));
        await orchestrator.RunAsync(context);

        Assert.That(verdictMap.AsReadOnly().Count, Is.EqualTo(5));
    }

    // ---- Fake helpers ----

    private sealed class FakeAiAgent(string verdict, bool oneFinding = false) : IAiAgent
    {
        public Task<IAiSession> OpenSessionAsync(AiSessionOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IAiSession>(new FakeAiSession(verdict, oneFinding));
    }

    private sealed class FakeAiSession(string verdict, bool oneFinding) : IAiSession
    {
        public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken = default)
        {
            string json;
            if (oneFinding)
                json = $"{{\"verdict\":\"{verdict}\",\"findings\":[{{\"lineStart\":1,\"lineEnd\":2,\"severity\":\"Info\",\"category\":\"Style\",\"message\":\"test\",\"suggestion\":null}}]}}";
            else
                json = $"{{\"verdict\":\"{verdict}\",\"findings\":[]}}";
            return Task.FromResult(json);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
