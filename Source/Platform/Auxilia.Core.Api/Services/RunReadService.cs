using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Read/query side of the Core's run view (populated by <see cref="RunTrackingService"/>). Lists
/// and counts are always scoped to what the caller may see (<see cref="RunAccessService"/>).
/// </summary>
public sealed class RunReadService(IDataAccess<CoreRunRecord> runs)
{
    /// <summary>
    /// Unscoped status read for Core-internal paths that authorized the caller already. Callers
    /// hold the DISPATCH id (RunAccepted.RunId = the command id) while the runner records under
    /// its own instance id — resolved through the stored alias, like the stream.
    /// </summary>
    public async Task<RunStatus?> GetAsync(Guid id, CancellationToken ct)
        => await GetRecordAsync(id, ct) is { } record ? ToStatus(record) : null;

    /// <summary>The runs <paramref name="viewer"/> may see, filtered and paged, newest first.</summary>
    public async Task<PagedResult<RunStatus>> QueryAsync(RunQuery query, RunViewer viewer, CancellationToken ct)
    {
        var all = (await runs.ReadAsync(ct)).Where(viewer.CanSee);
        if (!string.IsNullOrWhiteSpace(query.State))
            all = all.Where(r => r.State == query.State);
        if (!string.IsNullOrWhiteSpace(query.WorkflowType))
            all = all.Where(r => r.WorkflowType == query.WorkflowType);
        if (query.ConfigurationId is { } configId)
            all = all.Where(r => r.ConfigurationId == configId);
        var ordered = all.OrderByDescending(r => r.CreatedUtc).ToList();
        var page = ordered.Skip(query.Skip).Take(query.Take).Select(ToStatus).ToList();
        return new PagedResult<RunStatus>(page, ordered.Count, query.Skip, query.Take);
    }

    /// <summary>Aggregate counts over the runs <paramref name="viewer"/> may see — the dashboard's server-side stats.</summary>
    public async Task<RunStats> GetStatsAsync(RunViewer viewer, CancellationToken ct)
    {
        var all = (await runs.ReadAsync(ct)).Where(viewer.CanSee).ToList();
        var byState = all.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
        var active = all.Count(r => !CoreRunStates.IsTerminal(r.State));
        return new RunStats(all.Count, active, byState);
    }

    /// <summary>The raw record behind a run id (instance or dispatch alias), unscoped.</summary>
    internal async Task<CoreRunRecord?> GetRecordAsync(Guid id, CancellationToken ct)
        => await runs.ReadAsync(id, ct)
           ?? (await runs.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == id);

    public static RunStatus ToStatus(CoreRunRecord r) => new(
        r.Id, r.WorkflowType, r.State, r.ErrorMessage, r.CreatedUtc, r.ConfigurationId, r.ConfigurationName,
        r.CompletedUtc,
        HasTerminal: r.TerminalEndpoint is { Length: > 0 } && !CoreRunStates.IsTerminal(r.State))
    {
        RunsFor = r.RunsForPrincipalId,
        StartedBy = r.StartedByPrincipalId,
        Grants = AccessGrantEvaluator.Parse(r.GrantsJson),
        Reason = r.Reason,
    };
}
