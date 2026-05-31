using System.Text.Json;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class TwoEyesTests : ScenarioTestBase
{
    [Test]
    public async Task TwoEyes_Enabled_SecondaryApprovesSome()
    {
        // Primary produces 4 findings (one per file); secondary approves 2, rejects 2.
        var pullRequest = new FakePullRequestAccess(
            changedFiles:
            [
                File("src/A.cs"), File("src/B.cs"), File("src/C.cs"), File("src/D.cs"),
            ],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/A.cs"] = [Hunk("src/A.cs")],
                ["src/B.cs"] = [Hunk("src/B.cs")],
                ["src/C.cs"] = [Hunk("src/C.cs")],
                ["src/D.cs"] = [Hunk("src/D.cs")],
            });

        FakeAiAgent? primaryAi = null;
        primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn(() => primaryAi)]]));

        // Secondary opens a new session per finding — 4 findings, alternating Approved/Rejected
        FakeAiAgent? secondaryAi = null;
        secondaryAi = new FakeAiAgent(new Queue<ScriptedTurn>(
        [
            ApprovedTurn(() => secondaryAi), RejectedTurn(() => secondaryAi),
            ApprovedTurn(() => secondaryAi), RejectedTurn(() => secondaryAi),
        ]));

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            secondaryAi,
            OutputDir,
            twoEyes: new TwoEyesConfiguration { Enabled = true },
            writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));

        var metricsJson = await System.IO.File.ReadAllTextAsync(Path.Combine(OutputDir, "coverage-metrics.json"));
        var metrics = JsonSerializer.Deserialize<CoverageMetrics>(metricsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.That(metrics.StagedFindingCount, Is.EqualTo(4), "Primary staged 4 findings");
        Assert.That(metrics.SurvivingFindingCount, Is.EqualTo(2), "Secondary approved 2 of 4");

        var reviewJson = await System.IO.File.ReadAllTextAsync(Path.Combine(OutputDir, "code-review-result.json"));
        var review = JsonSerializer.Deserialize<CodeReviewResult>(reviewJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.That(review.Findings.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task TwoEyes_Disabled_NoSecondarySessionOpened()
    {
        var pullRequest = new FakePullRequestAccess(
            changedFiles: [File("src/Widget.cs")],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/Widget.cs"] = [Hunk("src/Widget.cs")],
            });

        FakeAiAgent? primaryAi = null;
        primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn(() => primaryAi)]]));
        var secondaryAi = new FakeAiAgent(new Queue<ScriptedTurn>()); // must not be opened

        var registry = new CodeReviewFakeRegistry(
            new FakeSourceControlAccess(),
            pullRequest,
            new FakeWorkItemAccess(),
            primaryAi,
            secondaryAi,
            OutputDir,
            twoEyes: new TwoEyesConfiguration { Enabled = false },
            writeBack: new WriteBackConfiguration { MinimumSeverity = FindingSeverity.Info });

        var result = await RunScenarioAsync(registry);

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
        Assert.That(secondaryAi.OpenSessionCallCount, Is.EqualTo(0),
            "Secondary reviewer must not be opened when two-eyes is disabled");

        var metricsJson = await System.IO.File.ReadAllTextAsync(Path.Combine(OutputDir, "coverage-metrics.json"));
        var metrics = JsonSerializer.Deserialize<CoverageMetrics>(metricsJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.That(metrics.SurvivingFindingCount, Is.EqualTo(metrics.StagedFindingCount),
            "All staged findings survive when two-eyes is disabled");
    }
}
