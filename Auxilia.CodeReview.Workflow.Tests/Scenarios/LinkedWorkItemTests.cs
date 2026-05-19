using Auxilia.CodeReview.Workflow;
using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class LinkedWorkItemTests : ScenarioTestBase
{
    [Test]
    public async Task WorkItemFails_IgnoreMode_Succeeds()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
            },
            linkedWorkItems: [new WorkItemReference("WI-1", null, null)]);

        // Work-item lookup throws; Ignore mode should absorb the error.
        var workItems = new FakeWorkItemAccess(failOnLookup: new Exception("Work-item service unavailable"));
        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn()]]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            workItems,
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            failureBehavior: WorkItemRetrievalFailureBehavior.Ignore);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success),
            "Ignore mode must absorb work-item retrieval failures");
    }

    [Test]
    public async Task WorkItemFails_FailMode_Fails()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
            },
            linkedWorkItems: [new WorkItemReference("WI-1", null, null)]);

        var workItems = new FakeWorkItemAccess(failOnLookup: new Exception("Work-item service unavailable"));
        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn()]]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            workItems,
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            failureBehavior: WorkItemRetrievalFailureBehavior.Fail);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Failed),
            "Fail mode must abort the workflow when work-item retrieval throws");
    }
}
