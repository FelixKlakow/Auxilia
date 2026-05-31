using Auxilia.CodeReview.Workflow.Compaction;
using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Mcp;
using Auxilia.CodeReview.Workflow.Verdicts;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auxilia.CodeReview.Workflow.PrimaryReview;

public sealed class PrimaryReviewOrchestrator(
    [FromKeyedServices("primary-reviewer")] IAiAgent aiAgent,
    IStagedFindingsStore findingsStore,
    ContextCompactionService compactionService,
    VerdictMap verdictMap,
    ILoggerFactory? loggerFactory = null)
{
    private long _currentTokenCount = 0;

    public async Task RunAsync(ReviewContext context, CancellationToken cancellationToken = default)
    {
        if (context.Files.Count == 0)
            return;

        var sink = new CodeReviewResultSinkMcpTools("code-review-sink", loggerFactory);
        try
        {
            await sink.StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "code-review-sink"), cancellationToken);

            var options = new AiSessionOptions { CapabilityTools = [sink] };
            IAiSession session;
            try
            {
                session = await aiAgent.OpenSessionAsync(options, cancellationToken);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                session = await aiAgent.OpenSessionAsync(options, cancellationToken);
            }

            try
            {
                foreach (var file in context.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ReviewFileAsync(session, sink, file, cancellationToken);
                    _currentTokenCount += EstimateTokens(file);

                    if (compactionService.ShouldCompact(_currentTokenCount))
                    {
                        await compactionService.CompactAsync(session, "Code review compaction", cancellationToken);
                        _currentTokenCount = 0;
                    }
                }
            }
            finally
            {
                await session.DisposeAsync();
            }
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await sink.StopAsync(stopCts.Token);
        }
    }

    private async Task ReviewFileAsync(
        IAiSession session, CodeReviewResultSinkMcpTools sink,
        ReviewableFile file, CancellationToken cancellationToken)
    {
        var hunkContent = string.Join("\n", file.Hunks.Select(h => h.Content));
        var prompt =
            $"Review the following file changes for '{file.FilePath}':\n{hunkContent}\n\n" +
            "Use the provided tools to record any findings and to record your verdict for this file.";

        await session.ExecuteAsync(prompt, cancellationToken);

        var findings = sink.DrainFindings();
        var verdict = sink.TakeFileVerdict();

        if (verdict is null)
        {
            loggerFactory?.CreateLogger<PrimaryReviewOrchestrator>()
                .LogWarning("No file verdict recorded via tool call for '{FilePath}'; defaulting to Reviewed.", file.FilePath);
            verdict = FileVerdict.Reviewed;
        }

        ApplyVerdict(file, verdict.Value, findings);
    }

    private void ApplyVerdict(ReviewableFile file, FileVerdict verdict, IReadOnlyList<StagedFinding> findings)
    {
        if (verdict == FileVerdict.Skipped)
        {
            if (file.Criticality == FileCriticality.Critical)
            {
                verdictMap.Record(file.FilePath, FileVerdict.Failed,
                    new SkipReason("Skipped verdict on Critical file"));
            }
            else
            {
                verdictMap.Record(file.FilePath, FileVerdict.Skipped,
                    new SkipReason("File skipped by primary reviewer"));
            }
        }
        else
        {
            foreach (var f in findings)
                findingsStore.Append(f);
            verdictMap.Record(file.FilePath, FileVerdict.Reviewed);
        }
    }

    private static long EstimateTokens(ReviewableFile file)
        => file.Hunks.Sum(h => (long)h.Content.Length) / 4;
}
