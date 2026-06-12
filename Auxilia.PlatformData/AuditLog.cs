using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData;

/// <summary>
/// Append-only writer for the platform audit log. Platform components are the only writers;
/// workflows can neither write nor read audit entries.
/// </summary>
public sealed class AuditLog(IDataAccess<AuditRecord> dataAccess, TimeProvider timeProvider)
{
    public Task AppendAsync(
        string actor, string action, string subject, string outcome,
        string? detailJson = null, CancellationToken ct = default)
        => dataAccess.SaveAsync(new AuditRecord
        {
            TimestampUtc = timeProvider.GetUtcNow(),
            Actor = actor,
            Action = action,
            Subject = subject,
            Outcome = outcome,
            DetailJson = detailJson
        }, ct);
}
