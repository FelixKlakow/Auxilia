namespace Auxilia.Workflows.SourceControl;

/// <summary>
/// Read-only source-control interface injected into workflows.
/// Provider packages implement this interface. Workflow code depends on it.
/// </summary>
public interface ISourceControl
{
    /// <summary>Local path where the repository snapshot is mounted. Use this path to access repository content directly.</summary>
    string WorkingPath { get; }

    /// <summary>Reads the text content of a file, optionally at a specific commit ref. Uses the head commit when <paramref name="commitRef"/> is null.</summary>
    Task<string> ReadFileContentAsync(string path, string? commitRef = null, CancellationToken cancellationToken = default);

    /// <summary>Lists file paths under <paramref name="directoryPath"/> (recursive), optionally at a specific commit ref.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string? directoryPath = null, string? commitRef = null, CancellationToken cancellationToken = default);

    /// <summary>Returns all files that changed between <paramref name="baseRef"/> and <paramref name="headRef"/>.</summary>
    Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default);
}
