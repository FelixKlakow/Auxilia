namespace Auxilia.CodeReview.Workflow;

public sealed record CoverageMetrics
{
    public required int TotalFiles { get; init; }
    public required int ReviewedFileCount { get; init; }
    public required int SkippedFileCount { get; init; }
    public required IReadOnlyList<SkippedFileEntry> SkippedFiles { get; init; }
    public required int FailedFileCount { get; init; }
    public required int StagedFindingCount { get; init; }
    public required int SurvivingFindingCount { get; init; }
}
