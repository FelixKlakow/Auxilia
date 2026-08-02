using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Verdicts;
using Auxilia.Workflows.PullRequestAccess;

namespace Auxilia.CodeReview.Workflow.Tests;

[TestFixture]
public sealed class CoverageMetricsProducerTests
{
    private static ReviewContext MakeContext(params ReviewableFile[] files) => new()
    {
        PullRequest = new PullRequestReference("PR-1", "main", "feature"),
        RepositoryWorkingPath = "/repo",
        Files = files,
        LinkedWorkItems = []
    };

    private static ReviewableFile MakeFile(string path) => new()
    {
        FilePath = path,
        ChangeKind = ChangeKind.Modified,
        Criticality = FileCriticality.Normal,
        Hunks = []
    };

    private static VerdictMap MakeVerdictMap(params (string path, FileVerdict v, string? reason)[] verdicts)
    {
        var map = new VerdictMap();
        foreach (var (path, v, reason) in verdicts)
            map.Record(path, v, reason is null ? null : new SkipReason(reason));
        return map;
    }

    private static IStagedFindingsStore MakeStore(int count)
    {
        var store = new StagedFindingsStore();
        for (int i = 0; i < count; i++)
            store.Append(new StagedFinding($"f{i}.cs", 1, 1, FindingSeverity.Info, "C", "m", null, "p"));
        return store;
    }

    private static CodeReviewResult MakeResult(int survivalCount) =>
        new()
        {
            PrIdentifier = "PR-1",
            Findings = Enumerable.Range(0, survivalCount).Select(i =>
                new ReviewFinding
                {
                    FilePath = $"f{i}.cs", LineStart = 1, LineEnd = 1, Severity = FindingSeverity.Info,
                    Category = "C", Message = "m", PrimaryReviewerAttribution = "p",
                    TwoEyesVerdict = SecondaryVerdict.NotReviewed
                }).ToArray(),
            Summary = "ok"
        };

    [Test]
    public void TotalFiles_EqualsContextFilesCount()
    {
        var ctx = MakeContext(MakeFile("a.cs"), MakeFile("b.cs"), MakeFile("c.cs"));
        var vm = MakeVerdictMap(
            ("a.cs", FileVerdict.Reviewed, null),
            ("b.cs", FileVerdict.Reviewed, null),
            ("c.cs", FileVerdict.Reviewed, null));
        var producer = new CoverageMetricsProducer(vm, MakeStore(0));
        var metrics = producer.Produce(ctx, MakeResult(0));

        Assert.That(metrics.TotalFiles, Is.EqualTo(3));
    }

    [Test]
    public void FileCounts_SumToTotalFiles()
    {
        var ctx = MakeContext(MakeFile("a.cs"), MakeFile("b.cs"), MakeFile("c.cs"), MakeFile("d.cs"));
        var vm = MakeVerdictMap(
            ("a.cs", FileVerdict.Reviewed, null),
            ("b.cs", FileVerdict.Skipped, "too large"),
            ("c.cs", FileVerdict.Failed, "timeout"),
            ("d.cs", FileVerdict.Reviewed, null));
        var producer = new CoverageMetricsProducer(vm, MakeStore(0));
        var metrics = producer.Produce(ctx, MakeResult(0));

        Assert.That(metrics.ReviewedFileCount + metrics.SkippedFileCount + metrics.FailedFileCount,
            Is.EqualTo(metrics.TotalFiles));
    }

    [Test]
    public void StagedFindingCount_EqualsStoreSize()
    {
        var ctx = MakeContext(MakeFile("a.cs"));
        var vm = MakeVerdictMap(("a.cs", FileVerdict.Reviewed, null));
        var producer = new CoverageMetricsProducer(vm, MakeStore(7));
        var metrics = producer.Produce(ctx, MakeResult(3));

        Assert.That(metrics.StagedFindingCount, Is.EqualTo(7));
    }

    [Test]
    public void SurvivingFindingCount_EqualsResultFindingsCount()
    {
        var ctx = MakeContext(MakeFile("a.cs"));
        var vm = MakeVerdictMap(("a.cs", FileVerdict.Reviewed, null));
        var producer = new CoverageMetricsProducer(vm, MakeStore(10));
        var metrics = producer.Produce(ctx, MakeResult(4));

        Assert.That(metrics.SurvivingFindingCount, Is.EqualTo(4));
    }

    [Test]
    public void SkippedFiles_ListContainsOnePerSkippedFile()
    {
        var ctx = MakeContext(MakeFile("a.cs"), MakeFile("b.cs"), MakeFile("c.cs"));
        var vm = MakeVerdictMap(
            ("a.cs", FileVerdict.Reviewed, null),
            ("b.cs", FileVerdict.Skipped, "binary file"),
            ("c.cs", FileVerdict.Skipped, "too large"));
        var producer = new CoverageMetricsProducer(vm, MakeStore(0));
        var metrics = producer.Produce(ctx, MakeResult(0));

        Assert.That(metrics.SkippedFiles, Has.Count.EqualTo(2));
        Assert.That(metrics.SkippedFiles.Select(f => f.FilePath), Is.EquivalentTo(new[] { "b.cs", "c.cs" }));
    }
}
