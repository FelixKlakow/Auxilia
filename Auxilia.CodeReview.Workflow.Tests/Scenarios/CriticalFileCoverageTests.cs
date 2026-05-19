using System.Text.Json;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class CriticalFileCoverageTests : ScenarioTestBase
{
    private const string CriticalPattern = "**/*.critical.cs";

    [Test]
    public async Task CriticalFile_Reviewed_Succeeds()
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
            criticalPatterns: [CriticalPattern]);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));

        var metricsJson = await System.IO.File.ReadAllTextAsync(Path.Combine(OutputDir, "coverage-metrics.json"));
        var metrics = JsonSerializer.Deserialize<CoverageMetrics>(metricsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.That(metrics.FailedFileCount, Is.EqualTo(0));
        Assert.That(metrics.ReviewedFileCount, Is.EqualTo(1));
    }

    [Test]
    public async Task CriticalFile_Skipped_Fails()
    {
        // Skipped verdict on a Critical file → FileVerdict.Failed recorded in coverage metrics.
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
            criticalPatterns: [CriticalPattern]);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Failed),
            "Workflow must fail when a critical file is skipped");

        var metricsJson = await System.IO.File.ReadAllTextAsync(Path.Combine(OutputDir, "coverage-metrics.json"));
        var metrics = JsonSerializer.Deserialize<CoverageMetrics>(metricsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.That(metrics.FailedFileCount, Is.EqualTo(1),
            "Critical file with Skipped verdict is recorded as FileVerdict.Failed");
    }

    [Test]
    public async Task CriticalFile_Failed_Fails()
    {
        // A critical file that the session cannot process (no matching turn) causes a workflow failure.
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Auth.critical.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Auth.critical.cs"] = [Hunk("src/Auth.critical.cs")],
            });

        // Provide an empty session (no turns) — FakeAiSession will throw when the file is reviewed.
        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[]]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir,
            criticalPatterns: [CriticalPattern]);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Failed),
            "Workflow must fail when the AI session cannot produce a verdict for a Critical file");
    }
}
