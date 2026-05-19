using Auxilia.CodeReview.Workflow.Compaction;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class ContextCompactionTests : ScenarioTestBase
{
    [Test]
    public async Task Compaction_TriggeredMidLoop_Succeeds()
    {
        // Three files; threshold=1 with fraction=1.0 means compaction fires after every file
        // (hunk "diff content" = 12 chars → ~3 tokens > threshold).
        // Expected OpenSessionAsync call count: 4 (1 initial + 1 per compaction × 3 files).
        const int fileCount = 3;

        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/A.cs"), File("src/B.cs"), File("src/C.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/A.cs"] = [Hunk("src/A.cs")],
                ["src/B.cs"] = [Hunk("src/B.cs")],
                ["src/C.cs"] = [Hunk("src/C.cs")],
            });

        // Each session is scripted with the expected turn sequence for its lifecycle.
        var sessions = new Queue<IReadOnlyList<ScriptedTurn>>(
        [
            // Session 1: review file A, then answer the compaction summary prompt
            [ReviewedTurn(), CompactionSummaryTurn()],
            // Session 2: accept the context-injection prompt, review file B, then compact
            [ContextInjectionTurn(), ReviewedTurn(), CompactionSummaryTurn()],
            // Session 3: accept context injection, review file C, then compact
            [ContextInjectionTurn(), ReviewedTurn(), CompactionSummaryTurn()],
            // Session 4: accept final context injection (opened by last compaction; no more files)
            [ContextInjectionTurn()],
        ]);

        var primaryAi = new FakeAiAgent(sessions);
        var compactionOptions = new ContextCompactionOptions
        {
            TokenLimitThreshold = 1,
            CompactionTriggerFraction = 1.0,
        };

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            compactionOptions: compactionOptions);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(primaryAi.OpenSessionCallCount, Is.GreaterThan(fileCount),
            "OpenSessionAsync should be called more times than the file count when compaction fires");
    }
}
