using System.Text.Json;
using Auxilia.AI;
using Auxilia.CodeReview.Workflow.Compaction;
using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.CodeReview.Workflow.Verdicts;
using Auxilia.Workflows.AiAgent;

namespace Auxilia.CodeReview.Workflow.PrimaryReview;

public sealed class PrimaryReviewOrchestrator(
    IAiInference aiInference,
    IStagedFindingsStore findingsStore,
    ContextCompactionService compactionService,
    VerdictMap verdictMap)
{
    private long _currentTokenCount = 0;

    public async Task RunAsync(ReviewContext context, CancellationToken cancellationToken = default)
    {
        if (context.Files.Count == 0)
            return;

        var session = await aiInference.CreateSessionAsync("primary-reviewer");
        try
        {
            foreach (var file in context.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReviewFileAsync(session, file, cancellationToken);
                _currentTokenCount += EstimateTokens(file);

                if (compactionService.ShouldCompact(_currentTokenCount))
                {
                    session = await compactionService.CompactAsync(session, aiInference, "primary-reviewer");
                    _currentTokenCount = 0;
                }
            }
        }
        finally
        {
            session.Dispose();
        }
    }

    private async Task ReviewFileAsync(IAgentSession session, ReviewableFile file, CancellationToken cancellationToken)
    {
        var hunkContent = string.Join("\n", file.Hunks.Select(h => h.Content));
        var prompt =
            $"Review the following file changes for '{file.FilePath}':\n{hunkContent}\n\n" +
            "Respond with JSON: {\"verdict\": \"Reviewed|Skipped\", \"findings\": " +
            "[{\"lineStart\": 1, \"lineEnd\": 1, \"severity\": \"Info\", \"category\": \"string\", " +
            "\"message\": \"string\", \"suggestion\": \"string\"}]}";

        var request = session.PrepareRequest(prompt);
        var response = await request.ExecuteRequestAsync(new ReviewResponseValidator(), cancellationToken);
        ApplyVerdict(file, response.Verdict, response.Findings);
    }

    private void ApplyVerdict(ReviewableFile file, string verdict, IEnumerable<FindingDto> findings)
    {
        if (verdict == "Skipped")
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
            {
                findingsStore.Append(new StagedFinding(
                    file.FilePath,
                    f.LineStart,
                    f.LineEnd,
                    Enum.TryParse<FindingSeverity>(f.Severity, true, out var sev) ? sev : FindingSeverity.Info,
                    f.Category,
                    f.Message,
                    f.Suggestion,
                    "primary-reviewer"));
            }
            verdictMap.Record(file.FilePath, FileVerdict.Reviewed);
        }
    }

    private static long EstimateTokens(ReviewableFile file)
        => file.Hunks.Sum(h => (long)h.Content.Length) / 4;

    private record FindingDto(int LineStart, int LineEnd, string Severity, string Category, string Message, string? Suggestion);

    private record ReviewResponse(string Verdict, IReadOnlyList<FindingDto> Findings);

    private sealed class ReviewResponseValidator : IAgentResultValidator<ReviewResponse>
    {
        public Task<ReviewResponse> ValidateAsync(IAgentRequest originalRequest, string agentTextOutput)
        {
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = JsonSerializer.Deserialize<ReviewResponse>(agentTextOutput, opts);
                return Task.FromResult(result ?? new ReviewResponse("Reviewed", []));
            }
            catch
            {
                return Task.FromResult(new ReviewResponse("Reviewed", []));
            }
        }
    }
}
