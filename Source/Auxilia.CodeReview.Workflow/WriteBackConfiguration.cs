using Auxilia.CodeReview.Workflow.Findings;

namespace Auxilia.CodeReview.Workflow;

public sealed record WriteBackConfiguration
{
    public FindingSeverity MinimumSeverity { get; init; } = FindingSeverity.Info;
    public bool PostSummaryToWorkItems { get; init; }
}
