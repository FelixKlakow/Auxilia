namespace Auxilia.Workflows.SourceControl;

public interface ISourceControlWriteAccess : ISourceControlAccess
{
    Task CreateBranchAsync(string branchName, string? fromRef = null, CancellationToken cancellationToken = default);
    Task WriteFileAsync(string relativePath, string content, CancellationToken cancellationToken = default);
    Task CommitAsync(string message, CancellationToken cancellationToken = default);
    Task PushAsync(CancellationToken cancellationToken = default);
}
