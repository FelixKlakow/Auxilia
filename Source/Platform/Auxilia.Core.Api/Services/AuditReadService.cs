using Auxilia.Core.Contracts;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Read/query side of the Core's centralized audit log (written by <see cref="Auxilia.PlatformData.AuditLog"/>).
/// The Core owns the audit store, so reads go straight to its own database — no cross-service access.
/// </summary>
public sealed class AuditReadService(
    IDataAccess<AuditRecord> audit, Auxilia.PlatformData.AuditLog auditLog,
    Auxilia.PlatformData.AuditCheckpointFile checkpoints)
{
    /// <summary>Re-verifies every writer chain of the stored trail (sequence, links, keyed hashes).</summary>
    public async Task<AuditIntegrityReport> VerifyAsync(CancellationToken ct)
    {
        var report = Auxilia.PlatformData.AuditChain.Verify(
            (await audit.ReadAsync(ct)).ToList(), auditLog.Integrity, await checkpoints.ReadAsync(ct));
        return new AuditIntegrityReport(report.Intact, report.Keyed, report.Chains, report.Records, report.Unchained,
            report.Breaks.Select(b => new AuditIntegrityBreak(b.ChainId, b.Sequence, b.RecordId, b.Problem)).ToList())
        {
            AnchoredChains = report.AnchoredChains
        };
    }

    public async Task<PagedResult<AuditEntry>> QueryAsync(AuditQuery query, CancellationToken ct)
    {
        var all = (await audit.ReadAsync(ct)).AsEnumerable();
        if (!string.IsNullOrWhiteSpace(query.Actor))
            all = all.Where(a => a.Actor == query.Actor);
        if (!string.IsNullOrWhiteSpace(query.Action))
            all = all.Where(a => a.Action == query.Action);
        if (!string.IsNullOrWhiteSpace(query.Subject))
            all = all.Where(a => a.Subject == query.Subject);
        if (query.FromUtc is { } from)
            all = all.Where(a => a.TimestampUtc >= from);
        if (query.ToUtc is { } to)
            all = all.Where(a => a.TimestampUtc < to);
        var ordered = all.OrderByDescending(a => a.TimestampUtc).ToList();
        var take = query.Take <= 0 ? 50 : query.Take;
        var page = ordered.Skip(query.Skip).Take(take).Select(ToDto).ToList();
        return new PagedResult<AuditEntry>(page, ordered.Count, query.Skip, take);
    }

    private static AuditEntry ToDto(AuditRecord a) =>
        new(a.Id, a.TimestampUtc, a.Actor, a.Action, a.Subject, a.Outcome, a.DetailJson) { Source = a.Source };
}
