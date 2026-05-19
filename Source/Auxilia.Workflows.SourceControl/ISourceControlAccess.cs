namespace Auxilia.Workflows.SourceControl;

/// <summary>
/// Behavioral contract for source-control access inside a workflow.
/// Replaces the <see cref="ISourceControl"/> placeholder.
/// </summary>
public interface ISourceControlAccess
{
    /// <summary>The Workspace-Manager-mounted repository path set by the slot handler.</summary>
    string WorkingPath { get; }

    Task<IReadOnlyList<string>> ListFilesAsync(string? relativePath = null, CancellationToken cancellationToken = default);

    Task<string> ReadFileContentAsync(string relativePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default);
}
