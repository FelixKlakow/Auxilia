using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Auxilia.PlatformData.Entities;

namespace Auxilia.PlatformData;

/// <summary>
/// The audit log's integrity key. Derived from the platform protection key when one is configured
/// (a store-only attacker cannot recompute the chain), otherwise a fixed development key — the
/// chain then still exposes careless edits and gaps, but not a deliberate rewrite.
/// </summary>
public sealed class AuditIntegrity
{
    private static readonly byte[] DevelopmentKey = SHA256.HashData("auxilia-audit-chain-unkeyed"u8);

    public AuditIntegrity(byte[]? protectionKey)
    {
        Keyed = protectionKey is { Length: > 0 };
        Key = Keyed
            ? HKDF.DeriveKey(HashAlgorithmName.SHA256, protectionKey!, 32, info: "auxilia-audit-chain"u8.ToArray())
            : DevelopmentKey;
    }

    internal byte[] Key { get; }

    /// <summary>Whether the chain is keyed by a secret the store does not hold.</summary>
    public bool Keyed { get; }
}

/// <summary>A writer's last durably stored record — what a checkpoint anchors.</summary>
public sealed record AuditChainHead(string ChainId, long Sequence, string Hash);

/// <summary>
/// A chain head signed with the audit key and kept OUTSIDE the audit store: whoever can write
/// the store can no longer cut a chain's tail (or drop a whole chain) undetected — verification
/// finds the checkpointed record missing or replaced.
/// </summary>
public sealed record AuditCheckpoint(string ChainId, long Sequence, string Hash, DateTimeOffset AtUtc, string Signature);

/// <summary>One integrity break found by <see cref="AuditChain.Verify"/>.</summary>
public sealed record AuditChainBreak(string ChainId, long Sequence, Guid? RecordId, string Problem);

/// <summary>The verdict over the stored audit trail.</summary>
public sealed record AuditChainReport(
    bool Intact, bool Keyed, int Chains, int Records, int Unchained, IReadOnlyList<AuditChainBreak> Breaks)
{
    /// <summary>Chains whose head an external checkpoint vouches for.</summary>
    public int AnchoredChains { get; init; }
}

/// <summary>
/// The tamper-evident chain over audit records: every writer (one chain per writer process)
/// numbers its records and HMACs each one together with its predecessor's hash. Altering a
/// record breaks its hash; deleting or inserting one breaks the sequence or the link.
/// </summary>
public static class AuditChain
{
    public static string Hash(byte[] key, AuditRecord record)
    {
        var canonical = string.Join('\u001f',
            record.Id.ToString("N"),
            record.TimestampUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            record.Actor, record.Action, record.Subject, record.Outcome,
            record.DetailJson ?? string.Empty, record.Source ?? string.Empty,
            record.ChainId ?? string.Empty,
            record.Sequence.ToString(CultureInfo.InvariantCulture),
            record.PreviousHash ?? string.Empty);
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static AuditCheckpoint Checkpoint(AuditIntegrity integrity, AuditChainHead head, DateTimeOffset atUtc)
        => new(head.ChainId, head.Sequence, head.Hash, atUtc,
            CheckpointSignature(integrity.Key, head.ChainId, head.Sequence, head.Hash, atUtc));

    private static string CheckpointSignature(byte[] key, string chainId, long sequence, string hash, DateTimeOffset atUtc)
        => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(string.Join('\u001f',
            "checkpoint", chainId, sequence.ToString(CultureInfo.InvariantCulture), hash,
            atUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))))).ToLowerInvariant();

    public static AuditChainReport Verify(
        IEnumerable<AuditRecord> records, AuditIntegrity integrity, IEnumerable<AuditCheckpoint>? checkpoints = null)
    {
        var all = records.ToList();
        var breaks = new List<AuditChainBreak>();
        var unchained = all.Count(r => r.ChainId is null);
        var chains = all.Where(r => r.ChainId is not null).GroupBy(r => r.ChainId!).ToList();
        foreach (var chain in chains)
        {
            string? previousHash = null;
            long expected = 1;
            foreach (var record in chain.OrderBy(r => r.Sequence))
            {
                if (record.Sequence != expected)
                    breaks.Add(new AuditChainBreak(chain.Key, expected, null,
                        record.Sequence > expected
                            ? $"records {expected}..{record.Sequence - 1} are missing"
                            : "duplicate sequence number"));
                if (record.PreviousHash != previousHash)
                    breaks.Add(new AuditChainBreak(chain.Key, record.Sequence, record.Id,
                        "the link to the preceding record does not match"));
                if (record.Hash != Hash(integrity.Key, record))
                    breaks.Add(new AuditChainBreak(chain.Key, record.Sequence, record.Id,
                        "the record's content does not match its hash"));
                previousHash = record.Hash;
                expected = record.Sequence + 1;
            }
        }

        // Every chain's newest genuine checkpoint must still find its record: a store-side
        // truncation (or a deleted chain) leaves the anchored head missing or replaced.
        var anchored = 0;
        foreach (var byChain in (checkpoints ?? []).GroupBy(c => c.ChainId))
        {
            var genuine = new List<AuditCheckpoint>();
            foreach (var checkpoint in byChain)
                if (checkpoint.Signature == CheckpointSignature(
                        integrity.Key, checkpoint.ChainId, checkpoint.Sequence, checkpoint.Hash, checkpoint.AtUtc))
                    genuine.Add(checkpoint);
                else
                    breaks.Add(new AuditChainBreak(checkpoint.ChainId, checkpoint.Sequence, null,
                        "a checkpoint's signature does not verify"));
            if (genuine.MaxBy(c => c.Sequence) is not { } head)
                continue;
            anchored++;
            var record = chains.FirstOrDefault(c => c.Key == head.ChainId)?.FirstOrDefault(r => r.Sequence == head.Sequence);
            if (record is null)
                breaks.Add(new AuditChainBreak(head.ChainId, head.Sequence, null,
                    "the chain no longer reaches its checkpointed head — records were removed"));
            else if (record.Hash != head.Hash)
                breaks.Add(new AuditChainBreak(head.ChainId, head.Sequence, record.Id,
                    "the checkpointed record was replaced"));
        }
        return new AuditChainReport(breaks.Count == 0, integrity.Keyed, chains.Count, all.Count, unchained, breaks)
        {
            AnchoredChains = anchored
        };
    }
}
