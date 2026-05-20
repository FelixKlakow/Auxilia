using Auxilia.ImplementationWorkflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.ImplementationWorkflow.Tests.Scenarios;

[TestFixture]
[Category("Component")]
public sealed class WriteBackFailureTests : ScenarioTestBase
{
    [Test]
    public async Task WriteBackFails_WarnBehavior_WorkflowSucceedsAndCompletedSignalEmitted()
    {
        var taskSource = new FakeTaskSourceAccess { PostCommentThrows = true };
        var workItem = DefaultWorkItem();
        taskSource.WorkItems[workItem.Id] = workItem;

        var registry = DefaultRegistry(
            workItem: workItem,
            taskSource: taskSource,
            configuration: new ImplementationWorkflowConfiguration
            {
                WriteBackBehavior = WriteBackBehavior.Warn,
                OutputDirectory = OutputDir
            });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));

        var completedSignals = registry.SignalEmitter.EmittedSignals
            .Where(s => s.SignalName == "Completed")
            .ToList();
        Assert.That(completedSignals, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task WriteBackFails_FailBehavior_WorkflowFailsAndNoSignalsEmitted()
    {
        var taskSource = new FakeTaskSourceAccess { PostCommentThrows = true };
        var workItem = DefaultWorkItem();
        taskSource.WorkItems[workItem.Id] = workItem;

        var registry = DefaultRegistry(
            workItem: workItem,
            taskSource: taskSource,
            configuration: new ImplementationWorkflowConfiguration
            {
                WriteBackBehavior = WriteBackBehavior.Fail,
                OutputDirectory = OutputDir
            });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Failed));
        Assert.That(registry.SignalEmitter.EmittedSignals, Is.Empty);
    }
}
