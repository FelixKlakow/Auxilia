using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Governance;

/// <summary>Per-workflow-type access list entries (the operators' day-to-day instrument).</summary>
public sealed class WorkflowTypeAccessStore(IDataAccess<WorkflowTypeAccessRecord> dataAccess)
{
    public async Task<IReadOnlyList<WorkflowTypeAccessRecord>> GetEntriesAsync(
        string workflowType, string action, CancellationToken ct = default)
    {
        var query = await dataAccess.ReadAsync(ct);
        return query
            .Where(e => e.WorkflowType == workflowType && e.Action == action)
            .ToList();
    }

    public Task GrantRoleAsync(string workflowType, string action, string roleName, CancellationToken ct = default)
        => dataAccess.SaveAsync(new WorkflowTypeAccessRecord
        {
            Id = WorkflowTypeAccessRecord.IdFor(workflowType, action, $"role:{roleName}"),
            WorkflowType = workflowType,
            Action = action,
            RoleName = roleName
        }, ct);

    public Task GrantPrincipalAsync(string workflowType, string action, Guid principalId, CancellationToken ct = default)
        => dataAccess.SaveAsync(new WorkflowTypeAccessRecord
        {
            Id = WorkflowTypeAccessRecord.IdFor(workflowType, action, $"principal:{principalId:D}"),
            WorkflowType = workflowType,
            Action = action,
            PrincipalId = principalId
        }, ct);

    public Task GrantGroupAsync(string workflowType, string action, Guid groupId, CancellationToken ct = default)
        => dataAccess.SaveAsync(new WorkflowTypeAccessRecord
        {
            Id = WorkflowTypeAccessRecord.IdFor(workflowType, action, $"group:{groupId:D}"),
            WorkflowType = workflowType,
            Action = action,
            GroupId = groupId
        }, ct);

    public Task<bool> RevokeRoleAsync(string workflowType, string action, string roleName, CancellationToken ct = default)
        => dataAccess.RemoveAsync(WorkflowTypeAccessRecord.IdFor(workflowType, action, $"role:{roleName}"), ct);

    public Task<bool> RevokePrincipalAsync(string workflowType, string action, Guid principalId, CancellationToken ct = default)
        => dataAccess.RemoveAsync(WorkflowTypeAccessRecord.IdFor(workflowType, action, $"principal:{principalId:D}"), ct);

    public Task<bool> RevokeGroupAsync(string workflowType, string action, Guid groupId, CancellationToken ct = default)
        => dataAccess.RemoveAsync(WorkflowTypeAccessRecord.IdFor(workflowType, action, $"group:{groupId:D}"), ct);

    /// <summary>Every entry of one workflow type, across actions (the admin read).</summary>
    public async Task<IReadOnlyList<WorkflowTypeAccessRecord>> ListAsync(
        string workflowType, CancellationToken ct = default)
        => (await dataAccess.ReadAsync(ct))
            .Where(e => e.WorkflowType == workflowType)
            .OrderBy(e => e.Action, StringComparer.Ordinal)
            .ToList();
}
