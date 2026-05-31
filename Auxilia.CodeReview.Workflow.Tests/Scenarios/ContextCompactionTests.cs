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
        // Three files; threshold=1 with fraction=1.0 means compaction fires after every file.
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/A.cs"), File("src/B.cs"), File("src/C.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/A.cs"] = [Hunk("src/A.cs")],
                ["src/B.cs"] = [Hunk("src/B.cs")],
                ["src/C.cs"] = [Hunk("src/C.cs")],
            });

        int compactionCount = 0;
        var session = new FakeAiSession([ReviewedTurn(), ReviewedTurn(), ReviewedTurn()])
        {
            CompactAsyncCallback = (_, _) => { compactionCount++; return Task.CompletedTask; }
        };

        var primaryAi = new FakeAiAgent(new Queue<FakeAiSession>([session]));
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
        Assert.That(primaryAi.OpenSessionCallCount, Is.EqualTo(1),
            "Only one session should be opened in the single-session compaction model");
        Assert.That(compactionCount, Is.EqualTo(3),
            "CompactAsync should be called once per file when compaction fires after every file");
    }
}
