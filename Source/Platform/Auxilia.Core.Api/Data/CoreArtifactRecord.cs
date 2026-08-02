using Auxilia.UniversalDataAccess;

namespace Auxilia.Core.Api.Data;

/// <summary>
/// Artifact metadata mirrored into the Core's own store from the runner's
/// <see cref="Auxilia.Workflows.Messaging.Messages.ArtifactPersistedEvent"/> bus fanout (same
/// isolation pattern as <see cref="CoreRunRecord"/> — the Core never reads the runner's index).
/// Keyed by the artifact id itself, so re-delivery is an idempotent upsert. This is what the
/// client-surface artifact query/SSE reads; payloads come from the shared payload backend.
/// </summary>
public sealed record CoreArtifactRecord : IEntity
{
    /// <summary>The artifact id assigned by the runner's store.</summary>
    public Guid Id { get; init; }

    public required string ArtifactType { get; init; }
    public required string WorkflowType { get; init; }
    public string WorkItemId { get; init; } = string.Empty;
    public Guid RunInstanceId { get; init; }
    public int Version { get; init; }
    public required string ContentHash { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}
