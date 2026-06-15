using Auxilia.PlatformData.Entities;

namespace Auxilia.PlatformData.Artifacts;

/// <summary>
/// Pluggable artifact persistence (ARCHITECTURE §4): the metadata index and the payload
/// storage are separate concerns behind one interface. Backends are a deployment choice
/// (filesystem for dev/single-node, blob or database later).
/// </summary>
public interface IArtifactStore
{
    /// <summary>
    /// Persists a payload and indexes it. The version is assigned automatically as the next
    /// in the (artifactType, workItemId) lineage.
    /// </summary>
    Task<ArtifactRecord> SaveAsync(
        string artifactType, string workflowType, string workItemId, Guid runInstanceId,
        Stream payload, CancellationToken ct = default);

    /// <summary>Opens the payload of an indexed artifact; null when unknown.</summary>
    Task<Stream?> OpenReadAsync(Guid artifactId, CancellationToken ct = default);

    /// <summary>The newest version of the (artifactType, workItemId) lineage; null when none.</summary>
    Task<ArtifactRecord?> ResolveLatestAsync(
        string artifactType, string workItemId, CancellationToken ct = default);

    /// <summary>All versions of the lineage, oldest first.</summary>
    Task<IReadOnlyList<ArtifactRecord>> GetLineageAsync(
        string artifactType, string workItemId, CancellationToken ct = default);

    Task<ArtifactRecord?> GetAsync(Guid artifactId, CancellationToken ct = default);
}
