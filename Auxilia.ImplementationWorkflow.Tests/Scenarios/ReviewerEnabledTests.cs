using Auxilia.ImplementationWorkflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.ImplementationWorkflow.Tests.Scenarios;

[TestFixture]
[Category("Component")]
public sealed class ReviewerEnabledTests : ScenarioTestBase
{
    [Test]
    public async Task AgentSucceeds_ReviewerEnabled_NoIssues_EmitsOnlyCompletedSignal()
    {
        var reviewerAgent = DefaultReviewerAgent("[]");

        var registry = DefaultRegistry(
            reviewerAgent: reviewerAgent,
            configuration: new ImplementationWorkflowConfiguration
            {
                ReviewerEnabled = true,
                OutputDirectory = OutputDir
            });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));

        var signals = registry.SignalEmitter.EmittedSignals.Select(s => s.SignalName).ToList();
        Assert.That(signals, Contains.Item("Completed"));
        Assert.That(signals, Does.Not.Contain("ReviewNotesFlagged"));

        Assert.That(registry.ReviewerAgent.OpenSessionCallCount, Is.EqualTo(1));
    }
}
