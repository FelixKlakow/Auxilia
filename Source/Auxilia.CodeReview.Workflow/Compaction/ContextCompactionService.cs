using Auxilia.Workflows.AiAgent;
using Microsoft.Extensions.Options;

namespace Auxilia.CodeReview.Workflow.Compaction;

public sealed class ContextCompactionService(IOptions<ContextCompactionOptions> options)
{
    private readonly ContextCompactionOptions _opts = options.Value;

    public bool ShouldCompact(long currentTokenCount)
        => currentTokenCount >= _opts.TokenLimitThreshold * _opts.CompactionTriggerFraction;

    public Task CompactAsync(IAiSession session, string focusDescription, CancellationToken cancellationToken = default)
        => session.CompactAsync(focusDescription, cancellationToken);
}
