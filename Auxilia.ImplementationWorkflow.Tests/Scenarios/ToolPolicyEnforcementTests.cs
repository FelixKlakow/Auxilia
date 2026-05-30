using Auxilia.ImplementationWorkflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.SourceControl;

namespace Auxilia.ImplementationWorkflow.Tests.Scenarios;

[TestFixture]
[Category("Component")]
public sealed class ToolPolicyEnforcementTests : ScenarioTestBase
{
    [Test]
    public async Task CommitDenied_CommitNotRecorded_WorkflowStateIsFailedAndFailedSignalEmitted()
    {
        var repository = new FakeSourceControlWriteAccess();
        repository.PolicyDenyList.Add(SourceControlOperation.Commit);

        var implementerAgent = new FakeAiAgent(
            new Queue<IReadOnlyList<ScriptedTurn>>([
                [new ScriptedTurn("", "Attempting to commit.",
                    ToolCall: () => repository.CommitAsync("my commit"))]
            ]));

        var registry = DefaultRegistry(
            repository: repository,
            implementerAgent: implementerAgent,
            configuration: new ImplementationWorkflowConfiguration { OutputDirectory = OutputDir });

        var result = await RunScenarioAsync(registry);

        Assert.That(repository.CommittedMessages, Is.Empty);
        Assert.That(result.State, Is.EqualTo(WorkflowState.Failed));

        var failedSignals = registry.SignalEmitter.EmittedSignals
            .Where(s => s.SignalName == "Failed")
            .ToList();
        Assert.That(failedSignals, Has.Count.EqualTo(1));
    }
}
