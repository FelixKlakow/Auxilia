using Auxilia.ImplementationWorkflow.Signals;
using Auxilia.ImplementationWorkflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.ImplementationWorkflow.Tests.Scenarios;

[TestFixture]
[Category("Component")]
public sealed class AgentFailureTests : ScenarioTestBase
{
    [Test]
    public async Task AgentThrows_EmitsFailedSignalWithPartialBranchName_WorkflowStateIsFailed()
    {
        var implementerAgent = new FakeAiAgent(
            new Queue<IReadOnlyList<ScriptedTurn>>([]),
            initialFailCount: 1);

        var registry = DefaultRegistry(
            implementerAgent: implementerAgent,
            configuration: new ImplementationWorkflowConfiguration { OutputDirectory = OutputDir });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Failed));

        var failedSignals = registry.SignalEmitter.EmittedSignals
            .Where(s => s.SignalName == "Failed")
            .ToList();
        Assert.That(failedSignals, Has.Count.EqualTo(1));

        var payload = (FailedSignalPayload)failedSignals[0].Payload;
        Assert.That(payload.PartialBranchName, Is.Not.Null);
        Assert.That(payload.FailureReason, Is.Not.Empty);
    }
}
