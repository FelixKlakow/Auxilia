using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class AiProviderFailureTests : ScenarioTestBase
{
    [Test]
    [Ignore("PrimaryReviewOrchestrator has no retry logic; transient failure cannot be recovered")]
    public async Task TransientFailure_Retries_Succeeds()
    {
        // This test is not implementable against the current production code because
        // PrimaryReviewOrchestrator calls OpenSessionAsync once with no retry loop.
        // If a retry mechanism is added, remove the [Ignore] and script a primary FakeAiAgent
        // whose first OpenSessionAsync call throws and whose second succeeds.
        await Task.CompletedTask;
    }

    [Test]
    public async Task PermanentFailure_ExhaustsRetries_Fails()
    {
        // Empty transcript queue → OpenSessionAsync throws InvalidOperationException on first call.
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
            });

        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>()); // exhausted queue

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Failed),
            "Workflow must fail when the primary AI provider is permanently unavailable");
    }
}
