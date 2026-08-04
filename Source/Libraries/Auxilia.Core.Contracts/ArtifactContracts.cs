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
    DateTimeOffset CreatedUtc);

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
