using Auxilia.ImplementationWorkflow;
using Auxilia.ImplementationWorkflow.Signals;
using Auxilia.ImplementationWorkflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.ImplementationWorkflow.Tests.Scenarios;

[TestFixture]
[Category("Component")]
public sealed class ReviewerFlagsIssuesTests : ScenarioTestBase
{
    [Test]
    public async Task AgentSucceeds_ReviewerEnabled_FlagsIssues_EmitsBothSignalsInOrder()
    {
        var reviewerAgent = DefaultReviewerAgent([
            new ReviewNote("Potential null reference", "src/Handler.cs", ReviewNoteSeverity.Warning)
        ]);

        var registry = DefaultRegistry(
            reviewerAgent: reviewerAgent,
            configuration: new ImplementationWorkflowConfiguration
            {
                ReviewerEnabled = true,
                OutputDirectory = OutputDir
            });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));

        var signals = registry.SignalEmitter.EmittedSignals;
        var signalNames = signals.Select(s => s.SignalName).ToList();
        Assert.That(signalNames, Contains.Item("Completed"));
        Assert.That(signalNames, Contains.Item("ReviewNotesFlagged"));

        var reviewFlaggedIdx = signalNames.IndexOf("ReviewNotesFlagged");
        var completedIdx = signalNames.IndexOf("Completed");
        Assert.That(reviewFlaggedIdx, Is.LessThan(completedIdx));

        var reviewFlaggedPayload = (ReviewNotesFlaggedSignalPayload)signals[reviewFlaggedIdx].Payload;
        Assert.That(reviewFlaggedPayload.ReviewNotes.Count, Is.GreaterThan(0));

        var summaryJson = await File.ReadAllTextAsync(Path.Combine(OutputDir, "implementation-summary.json"));
        Assert.That(summaryJson, Does.Contain("Potential null reference"));
    }
}
