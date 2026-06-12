using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.SteeringInstance.Workflows;

/// <summary>Durable run lifecycle records; Id is the workflow instance ID.</summary>
public sealed class WorkflowInstanceRegistry(
    IDataAccess<WorkflowInstanceRecord> dataAccess,
    TimeProvider timeProvider)
{
    public Task RegisterAsync(Guid instanceId, string workflowTypeName, CancellationToken ct = default)
        => dataAccess.SaveAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            WorkflowType = workflowTypeName,
            State = "Running",
            CreatedUtc = timeProvider.GetUtcNow()
        }, ct);

    public async Task<string?> GetWorkflowTypeAsync(Guid instanceId, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(instanceId, ct);
        return record?.WorkflowType;
    }

    /// <summary>Records a terminal or intermediate state; returns false for unknown instances.</summary>
    public async Task<bool> SetStateAsync(
        Guid instanceId, string state, string? errorMessage = null, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(instanceId, ct);
        if (record is null)
            return false;

        var isTerminal = state is "Success" or "Failed" or "Cancelled";
        await dataAccess.SaveAsync(record with
        {
            State = state,
            CompletedUtc = isTerminal ? timeProvider.GetUtcNow() : record.CompletedUtc,
            ErrorMessage = errorMessage ?? record.ErrorMessage
        }, ct);
        return true;
    }
}
