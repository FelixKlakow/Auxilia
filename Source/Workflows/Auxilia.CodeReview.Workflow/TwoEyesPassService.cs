using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Mcp;
using Auxilia.Workflows.AiAgent;
using Auxilia.Workflows.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auxilia.CodeReview.Workflow;

public sealed class TwoEyesPassService(
    [FromKeyedServices("secondary-reviewer")] IAiAgent secondaryAgent,
    TwoEyesConfiguration config,
    ILoggerFactory? loggerFactory = null)
{
    public async Task<IReadOnlyList<ReviewFinding>> RunAsync(
        IReadOnlyList<StagedFinding> staged,
        ReviewContext context,
        CancellationToken cancellationToken = default)
    {
        if (!config.Enabled)
        {
            return staged
                .Select(f => ReviewFinding.FromStaged(f, SecondaryVerdict.NotReviewed, null))
                .ToList()
                .AsReadOnly();
        }

        if (staged.Count == 0)
            return Array.Empty<ReviewFinding>();

        var results = new List<ReviewFinding>();
        foreach (var finding in staged)
        {
            var file = context.Files.FirstOrDefault(f => f.FilePath == finding.FilePath);
            var hunkContent = file != null
                ? string.Join("\n", file.Hunks.Select(h => h.Content))
                : string.Empty;

            var sink = new CodeReviewResultSinkMcpTools("secondary-review-sink", loggerFactory);
            try
            {
                await sink.StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "secondary-review-sink"), cancellationToken);

                var options = new AiSessionOptions { CapabilityTools = [sink] };
                await using var session = await secondaryAgent.OpenSessionAsync(options);

                var prompt =
                    $"Review this finding:\nFile: {finding.FilePath}, Lines {finding.LineStart}-{finding.LineEnd}\n" +
                    $"Severity: {finding.Severity}, Category: {finding.Category}\nMessage: {finding.Message}\n" +
                    $"Diff context:\n{hunkContent}\n\n" +
                    "Use the provided tools to record your verdict (Approved or Rejected) for this finding.";

                await session.ExecuteAsync(prompt, cancellationToken);

                var verdict = sink.TakeSecondaryVerdict();
                if (verdict is null)
                {
                    // A silent second reviewer never approves: the finding survives exactly
                    // as it would without the pass — unvetted and labelled so.
                    loggerFactory?.CreateLogger<TwoEyesPassService>()
                        .LogWarning("No secondary verdict recorded via tool call for finding in '{FilePath}'; the finding stays NotReviewed.", finding.FilePath);
                    results.Add(ReviewFinding.FromStaged(finding, SecondaryVerdict.NotReviewed, null));
                    continue;
                }

                results.Add(ReviewFinding.FromStaged(finding, verdict.Value, "secondary-reviewer"));
            }
            finally
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await sink.StopAsync(stopCts.Token);
            }
        }

        return results.AsReadOnly();
    }
}
