using System.Text.Json;

namespace Auxilia.CodeReview.Workflow;

public sealed class TerminalStatePublisher(string outputDirectory = "output")
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { WriteIndented = true };

    /// <summary>
    /// Serialises <paramref name="result"/> to <c>code-review-result.json</c> in the output directory
    /// and logs <paramref name="metrics"/> as a structured entry. When the platform output directory
    /// (<c>Workflow__OutputDirectory</c>) is set, the same files are written there for artifact persistence.
    /// </summary>
    /// <returns>
    /// <c>WorkflowState.Success</c> on success; throws on exception (caller may publish
    /// <c>WorkflowState.Failed</c> in a catch block).
    /// </returns>
    public async Task<string> PublishAsync(CodeReviewResult result, CoverageMetrics metrics)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        var metricsJson = JsonSerializer.Serialize(metrics, JsonOptions);

        await WriteAsync(outputDirectory, json, metricsJson);

        var platformOutputDirectory = Environment.GetEnvironmentVariable(
            Auxilia.Workflows.WorkflowEnvironmentVariables.OutputDirectory);
        if (!string.IsNullOrEmpty(platformOutputDirectory) && !SameDirectory(platformOutputDirectory, outputDirectory))
            await WriteAsync(platformOutputDirectory, json, metricsJson);

        return "Success";
    }

    private static async Task WriteAsync(string directory, string resultJson, string metricsJson)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "code-review-result.json"), resultJson);

        // Log coverage metrics as structured entry (written alongside the result)
        await File.WriteAllTextAsync(Path.Combine(directory, "coverage-metrics.json"), metricsJson);
    }

    private static bool SameDirectory(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}
