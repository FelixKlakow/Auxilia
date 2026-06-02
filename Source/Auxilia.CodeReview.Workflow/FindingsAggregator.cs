using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;

namespace Auxilia.CodeReview.Workflow;

public sealed class FindingsAggregator(TwoEyesPassService twoEyesPassService)
{
    public async Task<CodeReviewResult> AggregateAsync(IStagedFindingsStore store, ReviewContext context, CancellationToken cancellationToken = default)
    {
        var staged = store.Snapshot();
        var reviewed = await twoEyesPassService.RunAsync(staged, context, cancellationToken);

        var survivors = reviewed
            .Where(f => f.TwoEyesVerdict == SecondaryVerdict.Approved
                     || f.TwoEyesVerdict == SecondaryVerdict.NotReviewed)
            .ToList()
            .AsReadOnly();

        return new CodeReviewResult
        {
            PrIdentifier = context.PullRequest.PrIdentifier,
            Findings = survivors,
            Summary = $"Code review complete. {survivors.Count} finding(s) survived the review pass."
        };
    }
}
