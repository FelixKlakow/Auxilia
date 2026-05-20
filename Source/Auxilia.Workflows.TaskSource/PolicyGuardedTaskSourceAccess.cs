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
        if (!_policy.IsAllowed("task_source.get_work_item"))
            throw new ToolPolicyDeniedException("task_source.get_work_item", _slotName);
        return _inner.GetWorkItemAsync(id, cancellationToken);
    }

    public Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("task_source.get_work_items"))
            throw new ToolPolicyDeniedException("task_source.get_work_items", _slotName);
        return _inner.GetWorkItemsAsync(ids, cancellationToken);
    }

    public Task PostCommentAsync(string id, string comment, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("task_source.post_comment"))
            throw new ToolPolicyDeniedException("task_source.post_comment", _slotName);
        return _inner.PostCommentAsync(id, comment, cancellationToken);
    }

    public Task UpdateStatusAsync(string id, string newStatus, CancellationToken cancellationToken = default)
    {
        if (!_policy.IsAllowed("task_source.update_status"))
            throw new ToolPolicyDeniedException("task_source.update_status", _slotName);
        return _inner.UpdateStatusAsync(id, newStatus, cancellationToken);
    }
}
