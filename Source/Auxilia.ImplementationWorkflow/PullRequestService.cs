using Auxilia.ImplementationWorkflow.Context;
using Auxilia.Workflows.PullRequestAccess;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.ImplementationWorkflow;

public sealed class PullRequestService(
    [FromKeyedServices("pull-request")] IPullRequestAccess pullRequestAccess)
{
    public Task<string> OpenAsync(
        AgentCompletionResult agentResult,
        ImplementationContext context,
        CancellationToken cancellationToken = default)
    {
        var description = agentResult.AgentResponseText[..Math.Min(500, agentResult.AgentResponseText.Length)];

        var options = new PullRequestOptions(
            Title: $"impl: {context.WorkItem.Title}",
            SourceBranch: agentResult.BranchName,
            TargetBranch: "main",
            Description: description,
            LinkedWorkItemIds: [context.WorkItem.Id]);

        return pullRequestAccess.OpenPullRequestAsync(options, cancellationToken);
    }
}
