using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Personal dashboard pins: a principal marks a (run, view) pair to keep on their dashboard.
/// Everything is scoped to the owning principal; pinning the same view twice stays one pin.
/// </summary>
public sealed class DashboardPinService(
    IDataAccess<CoreDashboardPinRecord> pins,
    RunReadService runs,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<DashboardPin>> ListAsync(Guid principalId, CancellationToken ct)
        => (await pins.ReadAsync(ct))
            .Where(p => p.PrincipalId == principalId)
            .OrderByDescending(p => p.PinnedUtc)
            .Select(ToDto)
            .ToList();

    /// <summary>Pins a run view for the principal; null when the run is unknown.</summary>
    public async Task<DashboardPin?> PinAsync(Guid principalId, CreateDashboardPin request, CancellationToken ct)
    {
        // Callers may hold the dispatch id — resolve to the instance id like every other run read,
        // so the pin keeps working against the persisted view store.
        if (await runs.GetAsync(request.RunId, ct) is not { } run)
            return null;
        var record = new CoreDashboardPinRecord
        {
            Id = CoreDashboardPinRecord.IdFor(principalId, run.RunId, request.ViewName),
            PrincipalId = principalId,
            RunId = run.RunId,
            ViewName = request.ViewName,
            WorkflowType = run.WorkflowType,
            PinnedUtc = clock.GetUtcNow()
        };
        await pins.SaveAsync(record, ct);
        return ToDto(record);
    }

    /// <summary>Removes a pin; false when it does not exist or belongs to another principal.</summary>
    public async Task<bool> UnpinAsync(Guid principalId, Guid pinId, CancellationToken ct)
    {
        if (await pins.ReadAsync(pinId, ct) is not { } pin || pin.PrincipalId != principalId)
            return false;
        return await pins.RemoveAsync(pinId, ct);
    }

    private static DashboardPin ToDto(CoreDashboardPinRecord p)
        => new(p.Id, p.RunId, p.ViewName, p.WorkflowType, p.PinnedUtc);
}
