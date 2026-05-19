namespace Auxilia.CodeReview.Workflow.Findings;

public sealed record StagedFinding(
    string FilePath,
    int LineStart,
    int LineEnd,
    FindingSeverity Severity,
    string Category,
    string Message,
    string? Suggestion,
    string PrimaryReviewerAttribution);
