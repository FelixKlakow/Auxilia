using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Scenario")]
public sealed class EmptyPrTests : ScenarioTestBase
{
    [Test]
    public async Task NoPrFiles_WorkflowSucceeds_NoCommentsPosted()
    {
        var pullRequest = new FakePullRequestAccess();

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(0));
    }
}
