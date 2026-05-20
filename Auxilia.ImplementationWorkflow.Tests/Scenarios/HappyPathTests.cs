using Auxilia.ImplementationWorkflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.ImplementationWorkflow.Tests.Scenarios;

[TestFixture]
[Category("Component")]
public sealed class HappyPathTests : ScenarioTestBase
{
    [Test]
    public async Task AgentSucceeds_ReviewerDisabled_WritesBackAndEmitsCompletedSignal()
    {
        var repository = new FakeSourceControlWriteAccess();
        var taskSource = new FakeTaskSourceAccess();
        var workItem = DefaultWorkItem();
        taskSource.WorkItems[workItem.Id] = workItem;

        var pullRequest = new FakePullRequestAccess { PrUrl = "https://example.com/pr/42" };

        var registry = DefaultRegistry(
            workItem: workItem,
            repository: repository,
            taskSource: taskSource,
            pullRequest: pullRequest,
            configuration: new ImplementationWorkflowConfiguration
            {
                ReviewerEnabled = false,
                OutputDirectory = OutputDir
            });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(repository.CreatedBranches, Has.Count.EqualTo(1));
        Assert.That(repository.CreatedBranches[0], Does.StartWith("impl/"));
        Assert.That(taskSource.PostedComments, Has.Count.EqualTo(1));
        Assert.That(taskSource.PostedComments[0].Comment, Does.Contain("https://example.com/pr/42"));

        var summaryPath = Path.Combine(OutputDir, "implementation-summary.json");
        Assert.That(File.Exists(summaryPath), Is.True);

        var completedSignals = registry.SignalEmitter.EmittedSignals
            .Where(s => s.SignalName == "Completed")
            .ToList();
        Assert.That(completedSignals, Has.Count.EqualTo(1));
    }
}
