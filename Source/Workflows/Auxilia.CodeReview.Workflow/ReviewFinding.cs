using Auxilia.CodeReview.Workflow.Findings;

namespace Auxilia.CodeReview.Workflow;

public sealed record ReviewFinding
{
    public required string FilePath { get; init; }
    public required int LineStart { get; init; }
    public required int LineEnd { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required string Category { get; init; }
    public required string Message { get; init; }
    public string? Suggestion { get; init; }
    public required string PrimaryReviewerAttribution { get; init; }
    public required SecondaryVerdict TwoEyesVerdict { get; init; }
    public string? SecondaryReviewerAttribution { get; init; }

    public static ReviewFinding FromStaged(StagedFinding staged, SecondaryVerdict verdict, string? secondaryAttribution) =>
        new()
        {
            FilePath = staged.FilePath,
            LineStart = staged.LineStart,
            LineEnd = staged.LineEnd,
            Severity = staged.Severity,
            Category = staged.Category,
            Message = staged.Message,
            Suggestion = staged.Suggestion,
            PrimaryReviewerAttribution = staged.PrimaryReviewerAttribution,
            TwoEyesVerdict = verdict,
            SecondaryReviewerAttribution = secondaryAttribution
        };
}
