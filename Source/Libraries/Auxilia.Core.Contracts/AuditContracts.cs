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
    string? DetailJson)
{
    /// <summary>The platform service that recorded the entry (e.g. the runner a forwarded entry came from).</summary>
    public string? Source { get; init; }
}

/// <summary>One integrity break in the audit trail: where, and what no longer holds.</summary>
public sealed record AuditIntegrityBreak(string ChainId, long Sequence, Guid? RecordId, string Problem);

/// <summary>
/// The verdict over the stored audit trail: every writer's chain re-verified (sequence, links,
/// keyed hashes). <see cref="Keyed"/> tells whether the chain is keyed by a platform secret the
/// store does not hold (only then is a deliberate rewrite detectable, not just an edit).
/// </summary>
public sealed record AuditIntegrityReport(
    bool Intact,
    bool Keyed,
    int Chains,
    int Records,
    int Unchained,
    IReadOnlyList<AuditIntegrityBreak> Breaks)
{
    /// <summary>
    /// Chains whose head a signed checkpoint outside the store vouches for — truncating them in
    /// the store is detected (up to the last checkpoint interval).
    /// </summary>
    public int AnchoredChains { get; init; }
}

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
