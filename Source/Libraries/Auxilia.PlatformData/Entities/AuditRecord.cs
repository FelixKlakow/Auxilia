using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Append-only audit entry. Written by platform components only; workflows can neither
/// write nor read the audit log directly. Chained per writer (<see cref="AuditChain"/>).
/// </summary>
public sealed record AuditRecord : IEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; init; }
    /// <summary>Identity performing the action — service, user, or AI principal.</summary>
    public required string Actor { get; init; }
    /// <summary>Machine-readable action identifier, e.g. <c>workflow.registration.rejected</c>.</summary>
    public required string Action { get; init; }
    /// <summary>The resource the action applied to, e.g. a workflow instance ID.</summary>
    public required string Subject { get; init; }
    public required string Outcome { get; init; }
    public string? DetailJson { get; init; }

    /// <summary>The platform service that recorded the entry (e.g. the runner a forwarded entry came from).</summary>
    public string? Source { get; init; }

    /// <summary>The writer chain this record belongs to (one per writer process).</summary>
    public string? ChainId { get; init; }

    /// <summary>Position in <see cref="ChainId"/>, from 1.</summary>
    public long Sequence { get; init; }

    public string? PreviousHash { get; init; }

    /// <summary>HMAC over the record's content and <see cref="PreviousHash"/>.</summary>
    public string? Hash { get; init; }
}
