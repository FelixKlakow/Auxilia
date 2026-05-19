using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class WorkItemSummaryTests : ScenarioTestBase
{
    [Test]
    public async Task LinkedWorkItem_SummaryPosted_WhenPostSummaryEnabled()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
            },
            linkedWorkItems: [new WorkItemReference("WI-42", "My Story", null)]);

        var workItems = new FakeWorkItemAccess(
            items: new Dictionary<string, WorkItem>
            {
                ["WI-42"] = new WorkItem("WI-42", "My Story", "Do the thing", null, null, [])
            });

        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn()]]));

        var workItemComments = new List<(string Id, string Comment)>();
        var workItemAccess = new FakeWorkItemAccess(
            items: new Dictionary<string, WorkItem>
            {
                ["WI-42"] = new WorkItem("WI-42", "My Story", "Do the thing", null, null, [])
            });

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            workItemAccess,
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            writeBack: new WriteBackConfiguration
            {
                MinimumSeverity = FindingSeverity.Info,
                PostSummaryToWorkItems = true
            });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(1), "Finding comment posted to PR");
    }

    [Test]
    public async Task NoLinkedWorkItems_WorkflowSucceeds_NoWorkItemActivity()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
            });

        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn()]]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            writeBack: new WriteBackConfiguration
            {
                MinimumSeverity = FindingSeverity.Info,
                PostSummaryToWorkItems = true
            });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(1));
    }
}
