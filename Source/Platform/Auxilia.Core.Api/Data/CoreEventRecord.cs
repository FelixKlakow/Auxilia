using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// A platform event mirrored into the Core's own store from the <c>workflow.events</c> bus
/// exchange (same isolation pattern as <see cref="CoreArtifactRecord"/>). Keyed by the event id,
/// so re-delivery is an idempotent upsert. This is what the client-surface event query/SSE
/// catch-up reads; rows older than the configured retention are swept.
/// </summary>
public sealed record CoreEventRecord : IEntity
{
    /// <summary>The event id assigned by the publisher (deterministic for run-lifecycle events).</summary>
    public Guid Id { get; init; }

    public required string EventType { get; init; }
    public required string WorkflowType { get; init; }
    public string WorkItemId { get; init; } = string.Empty;
    public Guid? SourceRunId { get; init; }
    public string? PayloadJson { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}
