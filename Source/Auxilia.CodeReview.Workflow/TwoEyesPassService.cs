using System.Text.Json;
using Auxilia.CodeReview.Workflow.Context;
using Auxilia.CodeReview.Workflow.Findings;
using Auxilia.Workflows.AiAgent;
using Microsoft.Extensions.DependencyInjection;

namespace Auxilia.CodeReview.Workflow;

public sealed class TwoEyesPassService(
    [FromKeyedServices("secondary-reviewer")] IAiAgent secondaryAgent,
    TwoEyesConfiguration config)
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

            await using var session = await secondaryAgent.OpenSessionAsync();
            var prompt =
                $"Review this finding:\nFile: {finding.FilePath}, Lines {finding.LineStart}-{finding.LineEnd}\n" +
                $"Severity: {finding.Severity}, Category: {finding.Category}\nMessage: {finding.Message}\n" +
                $"Diff context:\n{hunkContent}\n\n" +
                "Respond with JSON: {\"verdict\": \"Approved|Rejected\"}";

            var verdictText = await session.ExecuteAsync(prompt);
            results.Add(ReviewFinding.FromStaged(finding, ParseVerdict(verdictText), "secondary-reviewer"));
        }

        return results.AsReadOnly();
    }

    private static SecondaryVerdict ParseVerdict(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var verdictStr = doc.RootElement.GetProperty("verdict").GetString() ?? "Approved";
            return Enum.TryParse<SecondaryVerdict>(verdictStr, true, out var v) ? v : SecondaryVerdict.Approved;
        }
        catch
        {
            return SecondaryVerdict.Approved;
        }
    }
}
