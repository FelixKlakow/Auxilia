using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class HappyPathTests : ScenarioTestBase
{
    [Test]
    public async Task TwoFiles_BothReviewed_FindingsPostedAsComments()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs"), File("src/Bar.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
                ["src/Bar.cs"] = [Hunk("src/Bar.cs")],
            });

        // One session handles all files – a single turn matches any file-review prompt
        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>(
        [
            [ReviewedTurn()]
        ]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(2),
            "One comment per file (each file produces one finding)");
    }

    [Test]
    public async Task SingleFile_Reviewed_FindingPostedAsComment()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Widget.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Widget.cs"] = [Hunk("src/Widget.cs")],
            });

        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>(
        [
            [ReviewedTurn()]
        ]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(1));
    }
}
