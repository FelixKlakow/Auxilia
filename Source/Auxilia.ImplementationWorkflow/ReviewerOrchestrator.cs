using Auxilia.ImplementationWorkflow.Context;
using Auxilia.ImplementationWorkflow.Mcp;
using Auxilia.Workflows;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.Mcp;
using Auxilia.Workflows.PullRequestAccess;
using Auxilia.Workflows.PullRequestAccess.Mcp;
using Auxilia.Workflows.SourceControl;
using Auxilia.Workflows.SourceControl.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auxilia.ImplementationWorkflow;

public sealed class ReviewerOrchestrator(
    [FromKeyedServices("reviewer-agent")] IAiAgent reviewerAgent,
    [FromKeyedServices("repository")] ISourceControlAccess repository,
    [FromKeyedServices("pull-request")] IPullRequestAccess pullRequestAccess,
    ImplementationWorkflowConfiguration configuration,
    ILoggerFactory? loggerFactory = null)
{
    public async Task<IReadOnlyList<ReviewNote>> RunAsync(
        AgentCompletionResult agentResult,
        ImplementationContext context,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.ReviewerEnabled)
            return [];

        var scmTools = new SourceControlAccessMcpTools("repository", repository, loggerFactory);
        var prTools = new PullRequestAccessMcpTools("pull-request", pullRequestAccess, loggerFactory);
        var sinkTools = new ImplementationReviewResultSinkMcpTools("review-sink", loggerFactory);

        try
        {
            await scmTools.StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "repository"), cancellationToken);
            await prTools.StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "pull-request"), cancellationToken);
            await sinkTools.StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "review-sink"), cancellationToken);

            var options = new AiSessionOptions
            {
                SystemPrompt = "You are an expert code reviewer. Review the implementation for quality, correctness, and adherence to acceptance criteria.",
                CapabilityTools = [scmTools, prTools, sinkTools]
            };

            await using var session = await reviewerAgent.OpenSessionAsync(options, cancellationToken);

            var prompt = BuildReviewerPrompt(context);
            await session.ExecuteAsync(prompt, cancellationToken);

            return sinkTools.DrainNotes();
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await scmTools.StopAsync(stopCts.Token);
            await prTools.StopAsync(stopCts.Token);
            await sinkTools.StopAsync(stopCts.Token);
        }
    }

    private static string BuildReviewerPrompt(ImplementationContext context)
    {
        var desc = string.IsNullOrEmpty(context.WorkItem.Description)
            ? "(no description)"
            : context.WorkItem.Description;

        return $"""
            ## Code Review Task

            **Work Item:** {context.WorkItem.Title}
            **Description:** {desc}

            Please review the implementation on the current branch. Use the repository tools to read changed files.
            Use the provided review-note tools to record any issues you find.
            """;
    }
}
