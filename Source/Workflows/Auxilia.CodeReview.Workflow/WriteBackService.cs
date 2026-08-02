using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.TaskSource;

namespace Auxilia.CodeReview.Workflow;

public sealed class WriteBackService(
    IPullRequestAccess pullRequestAccess,
    IWorkItemAccess workItemAccess,
    WriteBackConfiguration config)
{
    public async Task WriteBackAsync(CodeReviewResult result, IReadOnlyList<string> linkedWorkItemIds)
    {
        var thresholdFindings = result.Findings
            .Where(f => f.Severity <= config.MinimumSeverity)
            .ToList();

        foreach (var finding in thresholdFindings)
        {
            var body = $"**{finding.Severity} [{finding.Category}]** {finding.Message}" +
                       (finding.Suggestion is not null ? $"\n\nSuggestion: {finding.Suggestion}" : "");
            await pullRequestAccess.PostCommentAsync(body, finding.FilePath, finding.LineStart);
        }

        if (config.PostSummaryToWorkItems && linkedWorkItemIds.Count > 0)
        {
            var summary =
                $"Code Review Summary: {result.Summary}\n\n" +
                $"Total findings: {result.Findings.Count}\n" +
                $"Findings at or above threshold: {thresholdFindings.Count}";

            foreach (var workItemId in linkedWorkItemIds)
                await workItemAccess.PostCommentAsync(workItemId, summary);
        }
    }
}
