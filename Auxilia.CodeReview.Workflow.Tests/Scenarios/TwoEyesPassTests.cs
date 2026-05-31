using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class TwoEyesPassTests : ScenarioTestBase
{
    [Test]
    public async Task TwoEyesEnabled_SecondaryApproves_FindingPosted()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
            });

        FakeAiAgent? primaryAi = null;
        primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn(() => primaryAi)]]));
        // Secondary opens a new session per finding
        FakeAiAgent? secondaryAi = null;
        secondaryAi = new FakeAiAgent(new Queue<ScriptedTurn>([ApprovedTurn(() => secondaryAi)]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            secondaryAi,
            OutputDir,
            twoEyes: new TwoEyesConfiguration { Enabled = true },
            writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(1),
            "Approved finding should be posted");
    }

    [Test]
    public async Task TwoEyesEnabled_SecondaryRejects_FindingNotPosted()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
            });

        FakeAiAgent? primaryAi = null;
        primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn(() => primaryAi)]]));
        FakeAiAgent? secondaryAi = null;
        secondaryAi = new FakeAiAgent(new Queue<ScriptedTurn>([RejectedTurn(() => secondaryAi)]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            secondaryAi,
            OutputDir,
            twoEyes: new TwoEyesConfiguration { Enabled = true },
            writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(0),
            "Rejected finding should be filtered out");
    }

    [Test]
    public async Task TwoEyesDisabled_FindingsNotRoutedToSecondary()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
            });

        FakeAiAgent? primaryAi = null;
        primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn(() => primaryAi)]]));
        var secondaryAi = new FakeAiAgent(new Queue<ScriptedTurn>()); // must not be called

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            secondaryAi,
            OutputDir,
            twoEyes: new TwoEyesConfiguration { Enabled = false },
            writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(secondaryAi.OpenSessionCallCount, Is.EqualTo(0),
            "Secondary reviewer should not be invoked when two-eyes is disabled");
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(1));
    }
}
