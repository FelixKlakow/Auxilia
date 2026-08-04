using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Runner.Workflows;

/// <summary>Durable run lifecycle records; Id is the workflow instance ID.</summary>
public sealed class WorkflowInstanceRegistry(
    IDataAccess<WorkflowInstanceRecord> dataAccess,
    TimeProvider timeProvider)
{
    /// <summary>Creates the lifecycle record at dispatch time, before the container starts.</summary>
    public Task CreateAsync(
        Guid instanceId, string workflowTypeName, string state,
        Guid? ownerServiceId = null, string? dispatchCommandJson = null,
        Guid? workflowConfigurationId = null, string? workflowConfigurationName = null,
        CancellationToken ct = default)
        => dataAccess.SaveAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            WorkflowType = workflowTypeName,
            State = state,
            CreatedUtc = timeProvider.GetUtcNow(),
            OwnerServiceId = ownerServiceId,
            DispatchCommandJson = dispatchCommandJson,
            WorkflowConfigurationId = workflowConfigurationId,
            WorkflowConfigurationName = workflowConfigurationName
        }, ct);

    /// <summary>
    /// Marks an instance Running at registration. Preserves the dispatcher-created record when
    /// present; creates a fresh one for instances launched outside the dispatcher (dev mode).
    /// </summary>
    public async Task RegisterAsync(
        Guid instanceId, string workflowTypeName, string lifetime = "OneShot",
        string? outputsJson = null, string? viewsJson = null, CancellationToken ct = default)
    {
        var existing = await dataAccess.ReadAsync(instanceId, ct);
        if (existing is not null)
        {
            await dataAccess.SaveAsync(existing with
            {
                State = "Running",
                Lifetime = lifetime,
                OutputsJson = outputsJson ?? existing.OutputsJson,
                ViewsJson = viewsJson ?? existing.ViewsJson
            }, ct);
            return;
        }

        await dataAccess.SaveAsync(new WorkflowInstanceRecord
        {
            Id = instanceId,
            WorkflowType = workflowTypeName,
            State = "Running",
            Lifetime = lifetime,
            OutputsJson = outputsJson,
            ViewsJson = viewsJson,
            CreatedUtc = timeProvider.GetUtcNow()
        }, ct);
    }

    /// <summary>Running long-living instances of a workflow type (drain-and-replace targets).</summary>
    public async Task<IReadOnlyList<WorkflowInstanceRecord>> GetRunningLongLivingAsync(
        string workflowTypeName, CancellationToken ct = default)
    {
        var query = await dataAccess.ReadAsync(ct);
        return query
            .Where(r => r.WorkflowType == workflowTypeName &&
                        r.Lifetime == "LongLiving" &&
                        r.State == "Running")
            .ToList();
    }

    public async Task<WorkflowInstanceRecord?> GetAsync(Guid instanceId, CancellationToken ct = default)
        => await dataAccess.ReadAsync(instanceId, ct);

    public async Task<string?> GetWorkflowTypeAsync(Guid instanceId, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(instanceId, ct);
        return record?.WorkflowType;
    }

    /// <summary>Stamps the run's published web-terminal endpoint at launch (no-op for unknown instances).</summary>
    public async Task SetTerminalEndpointAsync(Guid instanceId, string endpoint, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(instanceId, ct);
        if (record is not null)
            await dataAccess.SaveAsync(record with { TerminalEndpoint = endpoint }, ct);
    }

    /// <summary>
    /// Persists the created container's identity + protected instance token — the re-adoption
    /// anchor: a restarted runner maps containers back to runs through this.
    /// </summary>
    public async Task SetContainerAsync(
        Guid instanceId, string containerId, string protectedInstanceToken, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(instanceId, ct);
        if (record is not null)
            await dataAccess.SaveAsync(record with
            {
                ContainerId = containerId,
                ProtectedInstanceToken = protectedInstanceToken
            }, ct);
    }

    /// <summary>Re-stamps ownership on re-adoption (the restarted runner has a fresh ServiceId).</summary>
    public async Task SetOwnerAsync(Guid instanceId, Guid ownerServiceId, CancellationToken ct = default)
    {
        var record = await dataAccess.ReadAsync(instanceId, ct);
        if (record is not null)
            await dataAccess.SaveAsync(record with { OwnerServiceId = ownerServiceId }, ct);
    }

    /// <summary>Every record not in a terminal state — the re-adoption candidates.</summary>
    public async Task<IReadOnlyList<WorkflowInstanceRecord>> GetNonTerminalAsync(CancellationToken ct = default)
    {
        var query = await dataAccess.ReadAsync(ct);
        return query
            .Where(r => r.State != "Success" && r.State != "Failed"
                        && r.State != "Cancelled" && r.State != "PreFlightFailed")
            .ToList();
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
