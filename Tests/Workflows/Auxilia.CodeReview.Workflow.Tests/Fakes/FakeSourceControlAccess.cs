using Auxilia.Workflows.SourceControl;

namespace Auxilia.CodeReview.Workflow.Tests.Fakes;

public sealed class FakeSourceControlAccess : ISourceControlAccess
{
    private readonly IReadOnlyList<string> _files;
    private readonly IReadOnlyDictionary<string, string> _content;
    private readonly IReadOnlyList<ChangedFile> _changedFiles;

    public FakeSourceControlAccess(
        string workingPath = "",
        IReadOnlyList<string>? files = null,
        IReadOnlyDictionary<string, string>? content = null,
        IReadOnlyList<ChangedFile>? changedFiles = null,
        bool throwOnMissingFile = false)
    {
        WorkingPath = workingPath;
        _files = files ?? [];
        _content = content ?? new Dictionary<string, string>();
        _changedFiles = changedFiles ?? [];
        ThrowOnMissingFile = throwOnMissingFile;
    }

    public string WorkingPath { get; }
    public bool ThrowOnMissingFile { get; }

    public Task<IReadOnlyList<string>> ListFilesAsync(string? relativePath = null, CancellationToken cancellationToken = default)
        => Task.FromResult(_files);

    public Task<string> ReadFileContentAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        if (_content.TryGetValue(relativePath, out var content))
            return Task.FromResult(content);

        if (ThrowOnMissingFile)
            throw new FileNotFoundException($"File not found: {relativePath}", relativePath);

        return Task.FromResult(string.Empty);
    }

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default)
        => Task.FromResult(_changedFiles);
}
