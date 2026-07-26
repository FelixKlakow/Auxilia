namespace Auxilia.Core.Contracts;

/// <summary>
/// One entry of the Core's append-only audit log, as returned to a reader. Mirrors the persisted
/// record: who (<see cref="Actor"/>) did what (<see cref="Action"/>) to which resource
/// (<see cref="Subject"/>), with the outcome and optional structured detail, at <see cref="TimestampUtc"/>.
/// </summary>
public sealed record AuditEntry(
    Guid Id,
    DateTimeOffset TimestampUtc,
    string Actor,
    string Action,
    string Subject,
    string Outcome,
    string? DetailJson);

/// <summary>
/// Filter for querying the audit log. Unset fields are ignored; results are newest-first and paged.
/// <see cref="Actor"/>/<see cref="Action"/>/<see cref="Subject"/> are exact matches; the time range
/// is inclusive of <see cref="FromUtc"/> and exclusive of <see cref="ToUtc"/>.
/// </summary>
public sealed record AuditQuery(
    string? Actor = null,
    string? Action = null,
    string? Subject = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int Skip = 0,
    int Take = 50);
