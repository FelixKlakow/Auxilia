using Auxilia.Workflows.AiAgent;
using Microsoft.Extensions.Options;

namespace Auxilia.CodeReview.Workflow.Compaction;

public sealed class ContextCompactionService(IOptions<ContextCompactionOptions> options)
{
    private readonly ContextCompactionOptions _opts = options.Value;

    public bool ShouldCompact(long currentTokenCount)
        => currentTokenCount >= _opts.TokenLimitThreshold * _opts.CompactionTriggerFraction;

    public async Task<IAiSession> CompactAsync(IAiSession currentSession, IAiAgent agent)
    {
        var summaryText = await currentSession.ExecuteAsync(
            "Summarize all findings recorded so far in a compact JSON block. " +
            "Include file, line range, severity, category, and message for each finding.");

        await currentSession.DisposeAsync();

        var newSession = await agent.OpenSessionAsync();

        if (!string.IsNullOrEmpty(summaryText))
            await newSession.ExecuteAsync(
                $"Context from prior reviews: {summaryText}. Continue reviewing remaining files.");

        return newSession;
    }
}
