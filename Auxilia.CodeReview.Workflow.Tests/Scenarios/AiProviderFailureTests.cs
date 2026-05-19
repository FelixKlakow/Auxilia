using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class AiProviderFailureTests : ScenarioTestBase
{
    [Test]
    public async Task TransientFailure_Retries_Succeeds()
    {
        // Primary AI fails on the first OpenSessionAsync call, then succeeds on the second.
        // PrimaryReviewOrchestrator retries once, so the workflow should complete successfully.
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
            });

        // initialFailCount=1 causes the first call to throw; the retry succeeds from the queue
        var primaryAi = new FakeAiAgent(
            new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn()]]),
            initialFailCount: 1);

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success),
            "One transient failure should be recovered by the retry logic");
    }

    [Test]
    public async Task PermanentFailure_ExhaustsRetries_Fails()
    {
        // All OpenSessionAsync calls throw — both the initial attempt and the retry fail.
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Foo.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Foo.cs"] = [Hunk("src/Foo.cs")],
            });

        // Empty queue → every OpenSessionAsync call throws "Transcript is exhausted..."
        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>());

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
        Assert.That(result.ErrorMessage, Does.Contain("session").Or.Contain("Transcript").Or.Contain("provider"),
            "Error message should reference the AI session or provider failure");
    }
}
