using Auxilia.ImplementationWorkflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.ImplementationWorkflow.Tests.Scenarios;

[TestFixture]
[Category("Component")]
public sealed class ReviewerDisabledTests : ScenarioTestBase
{
    [Test]
    public async Task ReviewerDisabled_ReviewerAgentNeverOpened_NoReviewNotesFlaggedSignal()
    {
        var registry = DefaultRegistry(
            configuration: new ImplementationWorkflowConfiguration
            {
                ReviewerEnabled = false,
                OutputDirectory = OutputDir
            });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(registry.ReviewerAgent.OpenSessionCallCount, Is.EqualTo(0));

        var signalNames = registry.SignalEmitter.EmittedSignals.Select(s => s.SignalName).ToList();
        Assert.That(signalNames, Does.Not.Contain("ReviewNotesFlagged"));
    }
}
