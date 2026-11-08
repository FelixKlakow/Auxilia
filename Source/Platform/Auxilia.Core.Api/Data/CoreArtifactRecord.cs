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

    /// <summary>Its kind when it has its own rules (<see cref="Auxilia.Core.Contracts.ArtifactKinds"/>); null for outputs and logs.</summary>
    public string? Kind { get; init; }

    /// <summary>When the Core deletes it; null when it is kept.</summary>
    public DateTimeOffset? ExpiresUtc { get; init; }

    /// <summary>The payload is encrypted with the Core's protection key (only the Core wrote it).</summary>
    public bool Encrypted { get; init; }

    /// <summary>The payload's media type when the Core knows it (a checkpointed folder is <c>application/gzip</c>).</summary>
    public string? MediaType { get; init; }

    public Auxilia.Core.Contracts.ArtifactDto ToDto()
        => new(Id, ArtifactType, WorkflowType, WorkItemId, RunInstanceId, Version, ContentHash, SizeBytes, CreatedUtc)
        {
            Kind = Kind,
            ExpiresUtc = ExpiresUtc,
            MediaType = MediaType,
        };
}
