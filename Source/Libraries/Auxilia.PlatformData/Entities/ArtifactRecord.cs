using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Metadata index entry for a persisted artifact (ARCHITECTURE §4): identity is
/// artifact type + producing workflow + work item + run + version. Versions of one
/// lineage (type + work item) are ordered, enabling "latest X for work item Y".
/// The payload lives in the pluggable payload store; the bus only ever carries
/// references (ID + content hash), never payloads.
/// </summary>
public sealed record ArtifactRecord : IEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Declared output name, e.g. "code-review-result".</summary>
    public required string ArtifactType { get; init; }
    public required string WorkflowType { get; init; }
    /// <summary>Originating work item; empty when the run had none.</summary>
    public string WorkItemId { get; init; } = string.Empty;
    public Guid RunInstanceId { get; init; }
    /// <summary>1-based, ordered within the (ArtifactType, WorkItemId) lineage.</summary>
    public int Version { get; init; }
    /// <summary>Hex SHA-256 of the payload.</summary>
    public required string ContentHash { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}
