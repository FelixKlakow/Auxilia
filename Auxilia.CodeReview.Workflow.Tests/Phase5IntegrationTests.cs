using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Verdicts;
using Auxilia.Workflows.Messaging.Messages;
using Auxilia.Workflows.Testing;

namespace Auxilia.CodeReview.Workflow.Tests;

/// <summary>
/// Integration tests covering Phase 5 end-to-end behaviour:
/// 1. Full workflow lifecycle via <see cref="WorkflowTestHarness"/> completes with <see cref="WorkflowState.Success"/>.
/// 2. <see cref="TerminalStatePublisher"/> writes <c>code-review-result.json</c> to the declared output path.
/// </summary>
[TestFixture]
public sealed class Phase5IntegrationTests
{
    [Test]
    public async Task WorkflowHarness_FullLifecycle_ProducesSuccessState()
    {
        var harness = WorkflowTestHarness
            .For(() => PullRequestReviewWorkflow.Main(["--test-harness"]))
            .WithDirective(WorkflowDirectiveKind.Run)
            .WithSlot("repository", "fake-source-control", new Dictionary<string, string>())
            .WithSlot("pull-request", "fake-pr-access", new Dictionary<string, string>())
            .WithSlot("work-items", "fake-task-source", new Dictionary<string, string>())
            .WithSlot("primary-reviewer", "fake-ai-agent", new Dictionary<string, string>())
            .WithSlot("secondary-reviewer", "fake-ai-agent", new Dictionary<string, string>())
            .WithTimeout(TimeSpan.FromSeconds(15))
            .Build();

        var result = await harness.RunAsync();

        Assert.That(result.State, Is.EqualTo(WorkflowState.Success));
    }

    [Test]
    public async Task TerminalStatePublisher_WritesCodeReviewResultJson()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"code-review-test-{Guid.NewGuid():N}");

        try
        {
            var publisher = new TerminalStatePublisher(outputDir);

            var result = new CodeReviewResult
            {
                PrIdentifier = "PR-100",
                Findings =
                [
                    new ReviewFinding
                    {
                        FilePath = "src/Foo.cs",
                        LineStart = 10, LineEnd = 12,
                        Severity = FindingSeverity.High,
                        Category = "Security",
                        Message = "Potential injection",
                        PrimaryReviewerAttribution = "primary-reviewer",
                        TwoEyesVerdict = SecondaryVerdict.NotReviewed
                    }
                ],
                Summary = "1 finding(s)"
            };

            var metrics = new CoverageMetrics
            {
                TotalFiles = 1,
                ReviewedFileCount = 1,
                SkippedFileCount = 0,
                SkippedFiles = [],
                FailedFileCount = 0,
                StagedFindingCount = 1,
                SurvivingFindingCount = 1
            };

            var status = await publisher.PublishAsync(result, metrics);

            Assert.That(status, Is.EqualTo("Success"));

            var outputPath = Path.Combine(outputDir, "code-review-result.json");
            Assert.That(File.Exists(outputPath), Is.True, "code-review-result.json should exist");

            var json = await File.ReadAllTextAsync(outputPath);
            Assert.That(json, Does.Contain("PR-100"));
            Assert.That(json, Does.Contain("src/Foo.cs"));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }
}
