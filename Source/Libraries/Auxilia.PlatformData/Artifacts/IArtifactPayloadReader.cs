namespace Auxilia.PlatformData.Artifacts;

/// <summary>
/// Read-only payload access by artifact id, WITHOUT the metadata index — for services that
/// mirror artifact metadata from the bus (their own store) but share the payload backend.
/// </summary>
public interface IArtifactPayloadReader
{
    /// <summary>Opens the payload; null when it does not exist in this backend.</summary>
    Task<Stream?> OpenReadAsync(Guid artifactId, CancellationToken ct = default);
}

/// <summary>Filesystem payload backend counterpart of <see cref="FileSystemArtifactStore"/>.</summary>
public sealed class FileSystemArtifactPayloadReader(
    PlatformDataSettings platformData,
    ArtifactStoreSettings storeSettings) : IArtifactPayloadReader
{
    public Task<Stream?> OpenReadAsync(Guid artifactId, CancellationToken ct = default)
    {
        var path = Path.Combine(storeSettings.ResolveRoot(platformData), artifactId.ToString("N"));
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }
}
