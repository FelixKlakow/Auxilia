namespace Auxilia.CodeReview.Workflow.Compaction;

public sealed class ContextCompactionOptions
{
    public double TokenLimitThreshold { get; set; } = 100_000;
    public double CompactionTriggerFraction { get; set; } = 0.8;
}
