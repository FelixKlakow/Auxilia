using System.Collections.Concurrent;
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
    PlatformDataSettings settings,
    ArtifactStoreSettings storeSettings) : IArtifactStore
{
    private const string PendingSuffix = ".pending";

    // Lineage versions are read-then-assigned, so concurrent saves into one lineage serialise on a
    // per-lineage gate. In-process only: the filesystem store is a single-process backend (one
    // singleton per runner over a local directory), so no cross-process coordination is needed.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lineageGates = new();

    private string Root => storeSettings.ResolveRoot(settings);

    public async Task<ArtifactRecord> SaveAsync(
        string artifactType, string workflowType, string workItemId, Guid runInstanceId,
        Stream payload, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Root);

        var gate = _lineageGates.GetOrAdd(LineageKey(artifactType, workItemId), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
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

            // The payload lands in a pending file first and only becomes readable (moved to its
            // final name) once the index row exists; any failure on the way removes the pending
            // file so nothing is orphaned on disk.
            var payloadPath = PayloadPathFor(record.Id);
            var pendingPath = payloadPath + PendingSuffix;
            try
            {
                long size;
                string hash;
                await using (var file = File.Create(pendingPath))
                {
                    using var sha = SHA256.Create();
                    await using (var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write, leaveOpen: true))
                        await payload.CopyToAsync(hashing, ct);
                    size = file.Length;
                    hash = Convert.ToHexString(sha.Hash!);
                }

                var indexed = record with { ContentHash = hash, SizeBytes = size };
                await index.SaveAsync(indexed, ct);
                try
                {
                    File.Move(pendingPath, payloadPath, overwrite: true);
                }
                catch
                {
                    await index.RemoveAsync(indexed.Id, CancellationToken.None);
                    throw;
                }

                return indexed;
            }
            catch
            {
                TryDelete(pendingPath);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
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

    private static string LineageKey(string artifactType, string workItemId)
        => artifactType + "\n" + workItemId;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a pending file that cannot be deleted right now is not worth masking
            // the original failure for.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
