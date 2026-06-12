using System.Text.Json;
using Auxilia.Governance;
using Auxilia.Governance.Policy;
using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.BackendService.Dashboard;

/// <summary>One pinned view on a principal's composed dashboard.</summary>
public sealed record DashboardPin(Guid InstanceId, string ViewName, string DisplayTitle);

/// <summary>
/// Dashboard composition (ARCHITECTURE §15): each principal owns one ordered list of pinned
/// views. Pinning is policy-checked like a live view subscription; only the owning principal
/// (or a holder of dashboard.manage) may change a dashboard. Every mutation is audited.
/// </summary>
public sealed class DashboardComposer(
    IDataAccess<DashboardRecord> dashboards,
    IDataAccess<WorkflowInstanceRecord> instances,
    IPolicyEngine policyEngine,
    AuditLog auditLog)
{
    public async Task<IReadOnlyList<DashboardPin>> GetPinsAsync(Guid principalId, CancellationToken ct = default)
    {
        var record = await dashboards.ReadAsync(DashboardRecord.IdFor(principalId), ct);
        return record is null ? [] : ParsePins(record.PinsJson);
    }

    public async Task PinAsync(Guid actorId, Guid ownerId, DashboardPin pin, CancellationToken ct = default)
    {
        await EnsureOwnershipAsync(actorId, ownerId, ct);

        var run = await instances.ReadAsync(pin.InstanceId, ct)
                  ?? throw new InvalidOperationException("Unknown run.");
        var decision = await policyEngine.EvaluateAsync(
            new PolicyContext(actorId, PermissionActions.ViewSubscribe, $"{pin.InstanceId}:{pin.ViewName}")
                { WorkflowType = run.WorkflowType }, ct);
        if (!decision.Allowed)
            throw new InvalidOperationException($"view.subscribe denied: {decision.Reason}");

        var pins = (await GetPinsAsync(ownerId, ct))
            .Where(p => p.InstanceId != pin.InstanceId || p.ViewName != pin.ViewName)
            .Append(pin)
            .ToList();
        await SavePinsAsync(ownerId, pins, ct);
        await auditLog.AppendAsync(actorId.ToString("D"), "dashboard.view-pinned",
            ownerId.ToString("D"), $"{pin.InstanceId}:{pin.ViewName}", ct: ct);
    }

    public async Task UnpinAsync(
        Guid actorId, Guid ownerId, Guid instanceId, string viewName, CancellationToken ct = default)
    {
        await EnsureOwnershipAsync(actorId, ownerId, ct);

        var pins = (await GetPinsAsync(ownerId, ct))
            .Where(p => p.InstanceId != instanceId || p.ViewName != viewName)
            .ToList();
        await SavePinsAsync(ownerId, pins, ct);
        await auditLog.AppendAsync(actorId.ToString("D"), "dashboard.view-unpinned",
            ownerId.ToString("D"), $"{instanceId}:{viewName}", ct: ct);
    }

    /// <summary>A principal manages its own dashboard; anyone else needs dashboard.manage.</summary>
    private async Task EnsureOwnershipAsync(Guid actorId, Guid ownerId, CancellationToken ct)
    {
        if (actorId == ownerId)
            return;

        var decision = await policyEngine.EvaluateAsync(
            new PolicyContext(actorId, PermissionActions.DashboardManage, $"dashboard:{ownerId:D}"), ct);
        if (!decision.Allowed)
            throw new InvalidOperationException($"dashboard.manage denied: {decision.Reason}");
    }

    private Task SavePinsAsync(Guid ownerId, IReadOnlyList<DashboardPin> pins, CancellationToken ct)
        => dashboards.SaveAsync(new DashboardRecord
        {
            Id = DashboardRecord.IdFor(ownerId),
            PrincipalId = ownerId,
            PinsJson = JsonSerializer.Serialize(pins)
        }, ct);

    private static IReadOnlyList<DashboardPin> ParsePins(string pinsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<List<DashboardPin>>(pinsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
