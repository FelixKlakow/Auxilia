using Auxilia.Workflows.Policy;

namespace Auxilia.Workflows.TaskSource;

/// <summary>
/// Policy-guarded decorator for <see cref="ITaskSourceAccess"/>.
/// Every <c>*Async</c> method checks <see cref="IToolPolicy.IsAllowed"/> before delegating.
/// </summary>
public sealed class PolicyGuardedTaskSourceAccess : ITaskSourceAccess
{
    private readonly ITaskSourceAccess _inner;
    private readonly IToolPolicy _policy;
    private readonly string _slotName;

    public PolicyGuardedTaskSourceAccess(ITaskSourceAccess inner, IToolPolicy policy, string slotName)
    {
        _inner = inner;
        _policy = policy;
        _slotName = slotName;
    }

    public Task<WorkItem?> GetWorkItemAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(TaskSourceOperation.GetWorkItem))
            throw new ToolPolicyDeniedException(TaskSourceOperation.GetWorkItem, _slotName);
        return _inner.GetWorkItemAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(TaskSourceOperation.GetWorkItems))
            throw new ToolPolicyDeniedException(TaskSourceOperation.GetWorkItems, _slotName);
        return _inner.GetWorkItemsAsync(ids, cancellationToken);
    }

    public Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(TaskSourceOperation.PostComment))
            throw new ToolPolicyDeniedException(TaskSourceOperation.PostComment, _slotName);
        return _inner.PostCommentAsync(id, comment, cancellationToken);
    }

    public Task UpdateStatusAsync(string id, string newStatus, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed(TaskSourceOperation.UpdateStatus))
            throw new ToolPolicyDeniedException(TaskSourceOperation.UpdateStatus, _slotName);
        return _inner.UpdateStatusAsync(id, newStatus, cancellationToken);
    }
}
