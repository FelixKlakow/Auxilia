using System.Text.Json;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class EveryFileCoveredTests : ScenarioTestBase
{
    [Test]
    public async Task AllFilesVisited_Succeeds()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/A.cs"), File("src/B.cs"), File("src/C.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/A.cs"] = [Hunk("src/A.cs")],
                ["src/B.cs"] = [Hunk("src/B.cs")],
                ["src/C.cs"] = [Hunk("src/C.cs")],
            });

        // A single session with a generic review turn handles all three files
        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn()]]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
    }

    [Test]
    public async Task NormalFileSkipped_StillSucceeds()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Generated.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Generated.cs"] = [Hunk("src/Generated.cs")],
            });

        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[SkippedTurn()]]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success),
            "A skipped Normal file does not fail the workflow");

        var metricsJson = await System.IO.File.ReadAllTextAsync(Path.Combine(OutputDir, "coverage-metrics.json"));
        var metrics = JsonSerializer.Deserialize<CoverageMetrics>(metricsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.That(metrics.SkippedFileCount, Is.EqualTo(1));
    }

    [Test]
    public async Task FileNotVisited_Fails()
    {
        // Session only has a turn matching File1. File2 causes the session to throw.
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/File1.cs"), File("src/File2.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/File1.cs"] = [Hunk("src/File1.cs")],
                ["src/File2.cs"] = [Hunk("src/File2.cs")],
            });

        var primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>(
        [
            [ReviewedTurn("src/File1.cs")], // only matches the first file's review prompt
        ]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            new FakeAiAgent(new Queue<ScriptedTurn>()),
            OutputDir);

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Failed),
            "Workflow must fail when the AI session cannot process a file");
    }
}
