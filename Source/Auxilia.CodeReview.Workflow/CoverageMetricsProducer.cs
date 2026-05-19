using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Verdicts;

namespace Auxilia.CodeReview.Workflow;

public sealed class CoverageMetricsProducer(VerdictMap verdictMap, IStagedFindingsStore findingsStore)
{
    public CoverageMetrics Produce(ReviewContext context, CodeReviewResult result)
    {
        var verdicts = verdictMap.AsReadOnly();
        int reviewed = 0, skipped = 0, failed = 0;
        var skippedFiles = new List<SkippedFileEntry>();

        foreach (var (path, (verdict, reason)) in verdicts)
        {
            switch (verdict)
            {
                case FileVerdict.Reviewed:
                    reviewed++;
                    break;
                case FileVerdict.Skipped:
                    skipped++;
                    skippedFiles.Add(new SkippedFileEntry(path, reason?.Explanation ?? "Skipped"));
                    break;
                case FileVerdict.Failed:
                    failed++;
                    break;
            }
        }

        return new CoverageMetrics
        {
            TotalFiles = context.Files.Count,
            ReviewedFileCount = reviewed,
            SkippedFileCount = skipped,
            SkippedFiles = skippedFiles.AsReadOnly(),
            FailedFileCount = failed,
            StagedFindingCount = findingsStore.Snapshot().Count,
            SurvivingFindingCount = result.Findings.Count
        };
    }
}
