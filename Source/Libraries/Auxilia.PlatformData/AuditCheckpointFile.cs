using System.Text.Json;

namespace Auxilia.PlatformData;

/// <summary>
/// Append-only JSON-lines file of signed <see cref="AuditCheckpoint"/>s, kept apart from the audit
/// store (point it at separate or write-once storage): the anchor that makes store-side
/// truncation of the audit trail detectable. As the writer's <see cref="IAuditAnchor"/> it records
/// the head after every stored record; a failed anchor write fails that append loudly rather than
/// leaving the trail silently unanchored.
/// </summary>
public sealed class AuditCheckpointFile(string path, AuditIntegrity integrity, TimeProvider clock) : IAuditAnchor
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Path { get; } = path;

    public Task AnchorAsync(AuditChainHead head, CancellationToken ct)
        => AppendAsync(AuditChain.Checkpoint(integrity, head, clock.GetUtcNow()), ct);

    public async Task AppendAsync(AuditCheckpoint checkpoint, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path, JsonSerializer.Serialize(checkpoint) + "\n", ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every readable checkpoint; an unparsable line is skipped (its absence weakens, never fakes, the anchor).</summary>
    public async Task<IReadOnlyList<AuditCheckpoint>> ReadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Path))
            return [];
        var checkpoints = new List<AuditCheckpoint>();
        foreach (var line in await File.ReadAllLinesAsync(Path, ct))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize<AuditCheckpoint>(line) is { } checkpoint)
                    checkpoints.Add(checkpoint);
            }
            catch (JsonException)
            {
                // Torn or tampered line — skipped.
            }
        }
        return checkpoints;
    }
}
