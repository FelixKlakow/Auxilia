using System.Text.Json;

namespace Auxilia.ClaudeCode.Workflow;

public sealed class SessionReportWriter(string outputDirectory)
{
    public const string FileName = "session-report.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public async Task<string> WriteAsync(SessionReport report, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, FileName);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, SerializerOptions), cancellationToken);
        return path;
    }
}
