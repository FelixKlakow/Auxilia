using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class WriteBackFailureTests : ScenarioTestBase
{
    [Test]
    public async Task WriteBack_PostCommentThrows_Fails()
    {
        // Primary produces one finding above the write-back threshold.
        // PostCommentAsync is configured to throw, so the write-back step fails.
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
            },
            throwOnPost: new Exception("PR host write-back failure"));

        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn()]]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Failed),
            "Workflow must fail when PostCommentAsync throws during write-back");
    }
}
