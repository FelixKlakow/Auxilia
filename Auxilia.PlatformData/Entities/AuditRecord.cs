using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Append-only audit entry. Written by platform components only; workflows can neither
/// write nor read the audit log directly.
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
}
