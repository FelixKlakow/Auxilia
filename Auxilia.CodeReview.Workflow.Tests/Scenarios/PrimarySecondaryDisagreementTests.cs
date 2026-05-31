using System.Text.Json;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Tests.Fakes;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests.Scenarios;

[TestFixture, Category("Component")]
public sealed class PrimarySecondaryDisagreementTests : ScenarioTestBase
{
    [Test]
    public async Task PrimarySecondary_Disagreement_OnlyApprovedPosted()
    {
        // Primary reviews 6 files, producing 6 staged findings.
        // Secondary alternates Approved / Rejected → 3 survive.
        var pullRequest = new FakePullRequestAccess(
            changedFiles:
            [
                File("src/A.cs"), File("src/B.cs"), File("src/C.cs"),
                File("src/D.cs"), File("src/E.cs"), File("src/F.cs"),
            ],
            diffHunks: new Dictionary<string, IReadOnlyList<DiffHunk>>
            {
                ["src/A.cs"] = [Hunk("src/A.cs")],
                ["src/B.cs"] = [Hunk("src/B.cs")],
                ["src/C.cs"] = [Hunk("src/C.cs")],
                ["src/D.cs"] = [Hunk("src/D.cs")],
                ["src/E.cs"] = [Hunk("src/E.cs")],
                ["src/F.cs"] = [Hunk("src/F.cs")],
            });

        FakeAiAgent? primaryAi = null;
        primaryAi = new FakeAiAgent(new Queue<IReadOnlyList<ScriptedTurn>>([[ReviewedTurn(() => primaryAi)]]));

        FakeAiAgent? secondaryAi = null;
        secondaryAi = new FakeAiAgent(new Queue<ScriptedTurn>(
        [
            ApprovedTurn(() => secondaryAi), RejectedTurn(() => secondaryAi),
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

        Assert.That(metrics.StagedFindingCount, Is.EqualTo(6));
        Assert.That(metrics.SurvivingFindingCount, Is.EqualTo(3));
        Assert.That(pullRequest.PostedComments.Count, Is.EqualTo(3),
            "Only the three approved findings are posted as comments");
    }
}
