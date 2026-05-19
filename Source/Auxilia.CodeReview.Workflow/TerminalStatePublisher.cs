using System.Text.Json;

namespace Auxilia.CodeReview.Workflow;

public sealed class TerminalStatePublisher(string outputDirectory = "output")
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    /// <summary>
    /// Serialises <paramref name="result"/> to <c>code-review-result.json</c> in the output directory
    /// and logs <paramref name="metrics"/> as a structured entry.
    /// </summary>
    /// <returns>
    /// <c>WorkflowState.Success</c> on success; throws on exception (caller may publish
    /// <c>WorkflowState.Failed</c> in a catch block).
    /// </returns>
    public async Task<string> PublishAsync(CodeReviewResult result, CoverageMetrics metrics)
    {
        Directory.CreateDirectory(outputDirectory);

        var outputPath = Path.Combine(outputDirectory, "code-review-result.json");
        var json = JsonSerializer.Serialize(result, JsonOptions);
        await File.WriteAllTextAsync(outputPath, json);

        // Log coverage metrics as structured entry (written alongside the result)
        var metricsPath = Path.Combine(outputDirectory, "coverage-metrics.json");
        var metricsJson = JsonSerializer.Serialize(metrics, JsonOptions);
        await File.WriteAllTextAsync(metricsPath, metricsJson);

        return "Success";
    }
}
