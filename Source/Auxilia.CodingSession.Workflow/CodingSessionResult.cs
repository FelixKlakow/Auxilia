using System.Text.Json;

namespace Auxilia.CodingSession.Workflow;

/// <summary>
/// The declared output artifact of a finished live session: which branch the work sits on
/// and which files changed — names only, file contents never leave the workspace this way.
/// </summary>
public sealed record CodingSessionResult(
    string Branch,
    IReadOnlyList<string> ChangedFiles,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    bool TimedOut)
{
    public const string FileName = "coding-session-result.json";

    public async Task WriteAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, FileName),
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }
}

/// <summary>One progress line of the session run's log view.</summary>
public sealed record SessionProgressEntry(string Phase, string Message);
