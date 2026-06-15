using System.Security.Cryptography;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Artifacts;

/// <summary>
/// Filesystem payload backend: payloads live under <c>{root}/{artifactId}</c>; the metadata
/// index uses the platform data layer, so lineage queries work identically across backends.
/// </summary>
public sealed class FileSystemArtifactStore(
    IDataAccess<ArtifactRecord> index,
    TimeProvider timeProvider,
    PlatformDataSettings settings) : IArtifactStore
{
    private string Root => Path.Combine(settings.JsonDirectory, "Artifacts");

    public async Task<ArtifactRecord> SaveAsync(
        string artifactType, string workflowType, string workItemId, Guid runInstanceId,
        Stream payload, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Root);

        var record = new ArtifactRecord
        {
            ArtifactType = artifactType,
            WorkflowType = workflowType,
            WorkItemId = workItemId,
            RunInstanceId = runInstanceId,
            Version = (await GetLineageAsync(artifactType, workItemId, ct)).Count + 1,
            ContentHash = string.Empty,
            CreatedUtc = timeProvider.GetUtcNow()
        };

        var payloadPath = PayloadPathFor(record.Id);
        long size;
        string hash;
        await using (var file = File.Create(payloadPath))
        {
            using var sha = SHA256.Create();
            await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write, leaveOpen: true))
                await payload.CopyToAsync(hashing, ct);
            size = file.Length;
            hash = Convert.ToHexString(sha.Hash!);
        }

        var indexed = record with { ContentHash = hash, SizeBytes = size };
        await index.SaveAsync(indexed, ct);
        return indexed;
    }

    public async Task<Stream?> OpenReadAsync(Guid artifactId, CancellationToken ct = default)
    {
        var record = await index.ReadAsync(artifactId, ct);
        if (record is null)
            return null;

        var path = PayloadPathFor(artifactId);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    public async Task<ArtifactRecord?> ResolveLatestAsync(
        string artifactType, string workItemId, CancellationToken ct = default)
        => (await GetLineageAsync(artifactType, workItemId, ct)).LastOrDefault();

    public async Task<IReadOnlyList<ArtifactRecord>> GetLineageAsync(
        string artifactType, string workItemId, CancellationToken ct = default)
    {
        var query = await index.ReadAsync(ct);
        return query
            .Where(r => r.ArtifactType == artifactType && r.WorkItemId == workItemId)
            .ToList()
            .OrderBy(r => r.Version)
            .ToList();
    }

    public Task<ArtifactRecord?> GetAsync(Guid artifactId, CancellationToken ct = default)
        => index.ReadAsync(artifactId, ct);

    private string PayloadPathFor(Guid artifactId) => Path.Combine(Root, artifactId.ToString("N"));
}
