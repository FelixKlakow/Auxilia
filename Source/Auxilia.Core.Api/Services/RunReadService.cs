using Auxilia.Core.Api.Data;
using Auxilia.Core.Contracts;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>Read/query side of the Core's run view (populated by <see cref="RunTrackingService"/>).</summary>
public sealed class RunReadService(IDataAccess<CoreRunRecord> runs)
{
    public async Task<RunStatus?> GetAsync(Guid id, CancellationToken ct)
    {
        if (await runs.ReadAsync(id, ct) is { } byInstance)
            return ToDto(byInstance);
        // Callers hold the DISPATCH id (RunAccepted.RunId = the command id) while the runner
        // records under its own instance id — resolve through the stored alias, like the stream.
        var byCommand = (await runs.ReadAsync(ct)).FirstOrDefault(r => r.CommandId == id);
        return byCommand is null ? null : ToDto(byCommand);
    }

    public async Task<PagedResult<RunStatus>> QueryAsync(RunQuery query, CancellationToken ct)
    {
        var all = (await runs.ReadAsync(ct)).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query.State))
            all = all.Where(r => r.State == query.State);
        if (!string.IsNullOrWhiteSpace(query.WorkflowType))
            all = all.Where(r => r.WorkflowType == query.WorkflowType);
        if (query.ConfigurationId is { } configId)
            all = all.Where(r => r.ConfigurationId == configId);
        var ordered = all.OrderByDescending(r => r.CreatedUtc).ToList();
        var page = ordered.Skip(query.Skip).Take(query.Take).Select(ToDto).ToList();
        return new PagedResult<RunStatus>(page, ordered.Count, query.Skip, query.Take);
    }

    private static RunStatus ToDto(CoreRunRecord r) => new(
        r.Id, r.WorkflowType, r.State, r.ErrorMessage, r.CreatedUtc, r.ConfigurationId, r.ConfigurationName);
}
