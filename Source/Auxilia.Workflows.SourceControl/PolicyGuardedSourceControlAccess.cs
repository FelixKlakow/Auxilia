using Auxilia.Workflows.Policy;

namespace Auxilia.Workflows.SourceControl;

/// <summary>
/// Policy-guarded decorator for <see cref="ISourceControlAccess"/>.
/// Every <c>*Async</c> method checks <see cref="IToolPolicy.IsAllowed"/> before delegating.
/// <see cref="WorkingPath"/> is not policy-guarded.
/// </summary>
public sealed class PolicyGuardedSourceControlAccess : ISourceControlAccess
{
    private readonly ISourceControlAccess _inner;
    private readonly IToolPolicy _policy;
    private readonly string _slotName;

    public PolicyGuardedSourceControlAccess(ISourceControlAccess inner, IToolPolicy policy, string slotName)
    {
        _inner = inner;
        _policy = policy;
        _slotName = slotName;
    }

    public string WorkingPath => _inner.WorkingPath;

    public Task<IReadOnlyList<string>> ListFilesAsync(string? relativePath = null, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("source_control.list_files"))
            throw new ToolPolicyDeniedException("source_control.list_files", _slotName);
        return _inner.ListFilesAsync(relativePath, cancellationToken);
    }

    public Task<string> ReadFileContentAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("source_control.read_file"))
            throw new ToolPolicyDeniedException("source_control.read_file", _slotName);
        return _inner.ReadFileContentAsync(relativePath, cancellationToken);
    }

    public Task<IReadOnlyList<ChangedFile>> GetChangedFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("source_control.get_changed_files"))
            throw new ToolPolicyDeniedException("source_control.get_changed_files", _slotName);
        return _inner.GetChangedFilesAsync(baseRef, headRef, cancellationToken);
    }
}
