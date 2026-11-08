namespace Auxilia.Core.Contracts;

/// <summary>
/// A persisted artifact's metadata as exposed on the Core client surface. Identity and
/// lineage follow the artifact store model: versions are ordered within the
/// (ArtifactType, WorkItemId) lineage; the payload is fetched separately by id.
/// </summary>
public sealed record ArtifactDto(
    Guid Id,
    string ArtifactType,
    string WorkflowType,
    string WorkItemId,
    Guid RunInstanceId,
    int Version,
    string ContentHash,
    long SizeBytes,
    DateTimeOffset CreatedUtc)
{
    /// <summary>What produced it (<see cref="ArtifactKinds"/>); null for a run's declared output or a platform log.</summary>
    public string? Kind { get; init; }

    /// <summary>When the Core deletes it; null when it is kept.</summary>
    public DateTimeOffset? ExpiresUtc { get; init; }

    /// <summary>The payload's media type when known — <c>application/gzip</c> for a checkpointed folder (a .tar.gz).</summary>
    public string? MediaType { get; init; }
}

/// <summary>Kinds of artifact with their own rules.</summary>
public static class ArtifactKinds
{
    /// <summary>
    /// A run checkpoint: the latest copy of a file or folder the workflow saved while it ran
    /// (a folder arrives as a .tar.gz). Encrypted at rest, kept for its lifetime
    /// (<see cref="ArtifactDto.ExpiresUtc"/>), and deletable by the principals the run belongs to.
    /// </summary>
    public const string Checkpoint = "checkpoint";
}

/// <summary>
/// Typed filter for artifact queries; all filters optional and combined with AND. Results are
/// newest-first, EXCEPT when <see cref="CreatedAfterUtc"/> is set — the catch-up shape pages
/// oldest-first so a reconnecting consumer drains a gap deterministically.
/// </summary>
public sealed record ArtifactQuery(
    string? ArtifactType = null,
    string? WorkItemId = null,
    Guid? RunId = null,
    DateTimeOffset? CreatedAfterUtc = null,
    int Skip = 0,
    int Take = 50);

/// <summary>
/// One frame of the artifact SSE stream (<c>GET /api/artifacts/stream</c>): an artifact was
/// persisted. The stream is server-side filtered — a subscriber receives only the artifact
/// types / work items it asked for, never the global feed.
/// </summary>
public sealed record ArtifactStreamEvent(
    ArtifactDto Artifact,
    DateTimeOffset TimestampUtc);
