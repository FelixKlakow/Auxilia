using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Scenario")]
public sealed class CriticalFilesTests : ScenarioTestBase
{
    private const string CriticalPattern = "**/*.critical.cs";

    [Test]
    public async Task CriticalFile_PrimarySkips_WorkflowSucceeds_NoFindingsPosted()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Auth.critical.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Auth.critical.cs"] = [Hunk("src/Auth.critical.cs")],
            });

        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[SkippedTurn()]]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            criticalPatterns: [CriticalPattern],
            writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(0),
            "Skipped critical file produces no surviving findings");
    }

    [Test]
    public async Task CriticalFile_PrimaryReviews_FindingPostedNormally()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Auth.critical.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Auth.critical.cs"] = [Hunk("src/Auth.critical.cs")],
            });

        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn()]]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            criticalPatterns: [CriticalPattern],
            writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(1),
            "Reviewed critical file posts findings normally");
    }

    [Test]
    public async Task NormalFile_PrimarySkips_NoFindingsPosted()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Utils.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Utils.cs"] = [Hunk("src/Utils.cs")],
            });

        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[SkippedTurn()]]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            criticalPatterns: [CriticalPattern],
            writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(0),
            "Normal skipped file produces no findings");
    }
}
