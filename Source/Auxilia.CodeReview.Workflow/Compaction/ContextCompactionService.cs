using Auxilia.AI;
using Auxilia.Workflows.AiAgent;
using Microsoft.Extensions.Options;

namespace Auxilia.CodeReview.Workflow.Compaction;

public sealed class ContextCompactionService(IOptions<ContextCompactionOptions> options)
{
    private readonly ContextCompactionOptions _opts = options.Value;

    public bool ShouldCompact(long currentTokenCount)
        => currentTokenCount >= _opts.TokenLimitThreshold * _opts.CompactionTriggerFraction;

    public async Task<IAgentSession> CompactAsync(
        IAgentSession currentSession,
        IAiInference inference,
        string slotName)
    {
        var summaryRequest = currentSession.PrepareRequest(
            "Summarize all findings recorded so far in a compact JSON block. " +
            "Include file, line range, severity, category, and message for each finding.");

        var summaryText = await summaryRequest.ExecuteRequestAsync(
            new TextResponseValidator(), CancellationToken.None);

        currentSession.Dispose();

        var newSession = await inference.CreateSessionAsync(slotName);

        if (!string.IsNullOrEmpty(summaryText))
        {
            var injectRequest = newSession.PrepareRequest(
                $"Context from prior reviews: {summaryText}. Continue reviewing remaining files.");
            await injectRequest.ExecuteRequestAsync(new TextResponseValidator(), CancellationToken.None);
        }

        return newSession;
    }

    private sealed class TextResponseValidator : IAgentResultValidator<string>
    {
        public Task<string> ValidateAsync(IAgentRequest originalRequest, string agentTextOutput)
            => Task.FromResult(agentTextOutput);
    }
}
