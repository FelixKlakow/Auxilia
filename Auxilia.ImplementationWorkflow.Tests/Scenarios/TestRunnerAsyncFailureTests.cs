using Auxilia.ImplementationWorkflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.ImplementationWorkflow.Tests.Scenarios;

[TestFixture]
[Category("Component")]
public sealed class TestRunnerAsyncFailureTests : ScenarioTestBase
{
    [Test]
    public async Task TestRunnerThrowsAsync_WorkflowStateIsFailedAndFailedSignalEmitted()
    {
        var testRunner = new FakeTestRunner { ThrowAsync = true };

        var implementerAgent = new FakeAiAgent(
            new Queue<IReadOnlyList<ScriptedTurn>>([
                [new ScriptedTurn("", "Running tests.",
                    ToolCall: () => testRunner.RunTestsAsync(new Auxilia.Workflows.TestRunner.TestRunRequest("dotnet test"), CancellationToken.None))]
            ]));

        var registry = DefaultRegistry(
            testRunner: testRunner,
            implementerAgent: implementerAgent,
            configuration: new ImplementationWorkflowConfiguration { OutputDirectory = OutputDir });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Failed));

        var failedSignals = registry.SignalEmitter.EmittedSignals
            .Where(s => s.SignalName == "Failed")
            .ToList();
        Assert.That(failedSignals, Has.Count.EqualTo(1));
    }
}
