using Auxilia.Workflows.Policy;

namespace Auxilia.Workflows.SourceControl;

/// <summary>
/// Policy-guarded decorator for <see cref="ISourceControlWriteAccess"/>.
/// Every <c>*Async</c> method checks <see cref="IToolPolicy.IsAllowed"/> before delegating.
/// <see cref="WorkingPath"/> is not policy-guarded.
/// </summary>
public sealed class PolicyGuardedSourceControlWriteAccess : ISourceControlWriteAccess
{
    private readonly ISourceControlWriteAccess _inner;
    private readonly IToolPolicy _policy;
    private readonly string _slotName;

    public PolicyGuardedSourceControlWriteAccess(ISourceControlWriteAccess inner, IToolPolicy policy, string slotName)
    {
        _inner = inner;
        _policy = policy;
        _slotName = slotName;
    }

    public string WorkingPath => _inner.WorkingPath;

    public Task<IReadOnlyList<string>> ListFilesAsync(string? relativePath = null, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(SourceControlOperation.ListFiles))
            throw new ToolPolicyDeniedException(SourceControlOperation.ListFiles, _slotName);
        return _inner.ListFilesAsync(relativePath, cancellationToken);
    }

    public Task<string> ReadFileContentAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(SourceControlOperation.ReadFile))
            throw new ToolPolicyDeniedException(SourceControlOperation.ReadFile, _slotName);
        return _inner.ReadFileContentAsync(relativePath, cancellationToken);
    }

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(SourceControlOperation.GetChangedFiles))
            throw new ToolPolicyDeniedException(SourceControlOperation.GetChangedFiles, _slotName);
        return _inner.GetChangedFilesAsync(baseRef, headRef, cancellationToken);
    }

    public Task CreateBranchAsync(string branchName, string? fromRef = null, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(SourceControlOperation.CreateBranch))
            throw new ToolPolicyDeniedException(SourceControlOperation.CreateBranch, _slotName);
        return _inner.CreateBranchAsync(branchName, fromRef, cancellationToken);
    }

    public Task WriteFileAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(SourceControlOperation.WriteFile))
            throw new ToolPolicyDeniedException(SourceControlOperation.WriteFile, _slotName);
        return _inner.WriteFileAsync(relativePath, content, cancellationToken);
    }

    public Task CommitAsync(string message, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(SourceControlOperation.Commit))
            throw new ToolPolicyDeniedException(SourceControlOperation.Commit, _slotName);
        return _inner.CommitAsync(message, cancellationToken);
    }

    public Task PushAsync(CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(SourceControlOperation.Push))
            throw new ToolPolicyDeniedException(SourceControlOperation.Push, _slotName);
        return _inner.PushAsync(cancellationToken);
    }
}
