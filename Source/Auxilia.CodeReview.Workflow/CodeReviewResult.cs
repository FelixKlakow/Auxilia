namespace Auxilia.CodeReview.Workflow;

public sealed record CodeReviewResult
{
    public required string PrIdentifier { get; init; }
    public required IReadOnlyList<ReviewFinding> Findings { get; init; }
    public required string Summary { get; init; }
}
