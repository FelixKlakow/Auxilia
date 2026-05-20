using Auxilia.Workflows.SourceControl;

namespace Auxilia.ImplementationWorkflow.Tests.Fakes;

public sealed class FakeSourceControlWriteAccess : ISourceControlWriteAccess
{
    public string WorkingPath { get; set; } = Path.GetTempPath();
    public Dictionary<string, string> FileContent { get; } = new();
    public List<string> CreatedBranches { get; } = new();
    public List<(string RelativePath, string Content)> WrittenFiles { get; } = new();
    public List<string> CommittedMessages { get; } = new();
    public int PushCallCount { get; private set; }
    public ISet<string> PolicyDenyList { get; } = new HashSet<string>();

    public Task CreateBranchAsync(string branchName, string? fromRef = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDenied("source_control.create_branch");
        CreatedBranches.Add(branchName);
        return Task.CompletedTask;
    }

    public Task WriteFileAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        ThrowIfDenied("source_control.write_file");
        WrittenFiles.Add((relativePath, content));
        return Task.CompletedTask;
    }

    public Task CommitAsync(string message, CancellationToken cancellationToken = default)
    {
        ThrowIfDenied("source_control.commit");
        CommittedMessages.Add(message);
        return Task.CompletedTask;
    }

    public Task PushAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDenied("source_control.push");
        PushCallCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string? relativePath = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> result = FileContent.Keys.ToList();
        return Task.FromResult(result);
    }

    public Task<string> ReadFileContentAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        if (FileContent.TryGetValue(relativePath, out var content))
            return Task.FromResult(content);

        throw new FileNotFoundException($"File not found: {relativePath}");
    }

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChangedFile> result = [];
        return Task.FromResult(result);
    }

    private void ThrowIfDenied(string operationKey)
    {
        if (PolicyDenyList.Contains(operationKey))
            throw new Auxilia.Workflows.Policy.ToolPolicyDeniedException(operationKey, "repository");
    }
}
