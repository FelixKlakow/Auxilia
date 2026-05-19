using System.Text.Json;
using Auxilia.AI;
using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.Workflows.AiAgent;

namespace Auxilia.CodeReview.Workflow;

public sealed class TwoEyesPassService(IAiInference aiInference, TwoEyesConfiguration config)
{
    public async Task<IReadOnlyList<ReviewFinding>> RunAsync(
        IReadOnlyList<StagedFinding> staged,
        ReviewContext context)
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

            var session = await aiInference.CreateSessionAsync(config.SecondarySlotName);
            try
            {
                var prompt =
                    $"Review this finding:\nFile: {finding.FilePath}, Lines {finding.LineStart}-{finding.LineEnd}\n" +
                    $"Severity: {finding.Severity}, Category: {finding.Category}\nMessage: {finding.Message}\n" +
                    $"Diff context:\n{hunkContent}\n\n" +
                    "Respond with JSON: {\"verdict\": \"Approved|Rejected\"}";

                var request = session.PrepareRequest(prompt);
                var verdict = await request.ExecuteRequestAsync(new VerdictValidator(), CancellationToken.None);
                results.Add(ReviewFinding.FromStaged(finding, verdict, config.SecondarySlotName));
            }
            finally
            {
                session.Dispose();
            }
        }

        return results.AsReadOnly();
    }

    private sealed class VerdictValidator : IAgentResultValidator<SecondaryVerdict>
    {
        public Task<SecondaryVerdict> ValidateAsync(IAgentRequest originalRequest, string agentTextOutput)
        {
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                using var doc = JsonDocument.Parse(agentTextOutput);
                var verdictStr = doc.RootElement.GetProperty("verdict").GetString() ?? "Approved";
                return Task.FromResult(
                    Enum.TryParse<SecondaryVerdict>(verdictStr, true, out var v) ? v : SecondaryVerdict.Approved);
            }
            catch
            {
                return Task.FromResult(SecondaryVerdict.Approved);
            }
        }
    }
}
