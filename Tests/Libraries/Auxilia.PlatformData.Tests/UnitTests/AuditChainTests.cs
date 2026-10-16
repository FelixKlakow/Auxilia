using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.PlatformData.Tests.UnitTests;

/// <summary>
/// The audit trail is tamper-evident: every writer chains its records (sequence + keyed hash
/// over the predecessor), so an edited, deleted or inserted record is reported — and with a
/// platform key, a store-only attacker cannot recompute the chain.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class AuditChainTests
{
    private static readonly AuditIntegrity Keyed = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private static async Task<(InMemoryDataAccess<AuditRecord> Store, AuditLog Log)> WriteAsync(
        int count, AuditIntegrity integrity, bool batched = false)
    {
        var store = new InMemoryDataAccess<AuditRecord>();
        var log = new AuditLog(store, TimeProvider.System, new AuditLogSettings { BatchedWrites = batched }, integrity);
        for (var i = 0; i < count; i++)
            await log.AppendAsync("svc", "action", $"subject-{i}", "ok");
        await log.FlushAsync();
        return (store, log);
    }

    private static async Task<List<AuditRecord>> AllAsync(InMemoryDataAccess<AuditRecord> store)
        => (await store.ReadAsync()).ToList();

    [TestCase(false)]
    [TestCase(true)]
    public async Task AnUntouchedTrail_IsIntact(bool batched)
    {
        var (store, _) = await WriteAsync(5, Keyed, batched);

        var report = AuditChain.Verify(await AllAsync(store), Keyed);

        Assert.That(report.Intact, Is.True, string.Join("; ", report.Breaks));
        Assert.That((report.Chains, report.Records, report.Keyed), Is.EqualTo((1, 5, true)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ATruncatedTail_IsDetected_AgainstTheExternalCheckpoint(bool batched)
    {
        var (store, log) = await WriteAsync(5, Keyed, batched);
        var checkpoint = AuditChain.Checkpoint(Keyed, log.PersistedHead!, DateTimeOffset.UtcNow);
        Assert.That(checkpoint.Sequence, Is.EqualTo(5), "the anchor is the newest STORED record");
        Assert.That(AuditChain.Verify(await AllAsync(store), Keyed, [checkpoint]).AnchoredChains, Is.EqualTo(1));

        // Cutting the tail leaves a chain that is internally consistent — only the anchor knows.
        foreach (var record in (await AllAsync(store)).Where(r => r.Sequence >= 4))
            await store.RemoveAsync(record.Id);
        var report = AuditChain.Verify(await AllAsync(store), Keyed, [checkpoint]);

        Assert.That(report.Intact, Is.False);
        Assert.That(report.Breaks.Single().Problem, Does.Contain("checkpointed head"));
    }

    [Test]
    public async Task ADeletedChain_IsDetected_AgainstTheExternalCheckpoint()
    {
        var (store, log) = await WriteAsync(3, Keyed);
        var checkpoint = AuditChain.Checkpoint(Keyed, log.PersistedHead!, DateTimeOffset.UtcNow);
        foreach (var record in await AllAsync(store))
            await store.RemoveAsync(record.Id);

        Assert.That(AuditChain.Verify(await AllAsync(store), Keyed, [checkpoint]).Intact, Is.False);
    }

    [Test]
    public async Task AForgedCheckpoint_IsReported()
    {
        var (store, log) = await WriteAsync(3, Keyed);
        var forged = AuditChain.Checkpoint(Keyed, log.PersistedHead!, DateTimeOffset.UtcNow) with { Sequence = 1 };

        var report = AuditChain.Verify(await AllAsync(store), Keyed, [forged]);

        Assert.That(report.Breaks.Single().Problem, Does.Contain("signature"));
    }

    [Test]
    public async Task Checkpoints_RoundTripThroughTheCheckpointFile()
    {
        var (_, log) = await WriteAsync(2, Keyed);
        var file = new AuditCheckpointFile(
            Path.Combine(Path.GetTempPath(), $"auxilia-cp-{Guid.NewGuid():N}.jsonl"), Keyed, TimeProvider.System);
        try
        {
            var checkpoint = AuditChain.Checkpoint(Keyed, log.PersistedHead!, DateTimeOffset.UtcNow);
            await file.AppendAsync(checkpoint);

            Assert.That(await file.ReadAsync(), Is.EqualTo(new[] { checkpoint }));
        }
        finally
        {
            File.Delete(file.Path);
        }
    }

    [Test]
    public async Task AnEditedRecord_IsDetected()
    {
        var (store, _) = await WriteAsync(5, Keyed);
        var victim = (await AllAsync(store)).Single(r => r.Sequence == 3);
        await store.SaveAsync(victim with { Outcome = "denied" });

        var report = AuditChain.Verify(await AllAsync(store), Keyed);

        Assert.That(report.Intact, Is.False);
        Assert.That(report.Breaks.Single().Problem, Does.Contain("content"));
    }

    [Test]
    public async Task ADeletedRecord_IsDetected()
    {
        var (store, _) = await WriteAsync(5, Keyed);
        await store.RemoveAsync((await AllAsync(store)).Single(r => r.Sequence == 2).Id);

        var report = AuditChain.Verify(await AllAsync(store), Keyed);

        Assert.That(report.Intact, Is.False);
        Assert.That(report.Breaks.Select(b => b.Problem), Has.Some.Contains("missing"));
    }

    [Test]
    public async Task ARecordRewrittenWithoutTheKey_IsDetected()
    {
        var (store, _) = await WriteAsync(3, Keyed);
        var victim = (await AllAsync(store)).Single(r => r.Sequence == 3);
        // An attacker with store access recomputes the hash — but without the platform key the
        // best available is another key (here: the unkeyed development scheme).
        var forged = victim with { Outcome = "denied", Hash = null };
        var unkeyed = new InMemoryDataAccess<AuditRecord>();
        var forger = new AuditLog(unkeyed, TimeProvider.System, integrity: new AuditIntegrity(null));
        await forger.AppendAsync(forged.Actor, forged.Action, forged.Subject, forged.Outcome);
        forged = forged with { Hash = (await unkeyed.ReadAsync()).Single().Hash };
        await store.SaveAsync(forged);

        Assert.That(AuditChain.Verify(await AllAsync(store), Keyed).Intact, Is.False);
    }

    [Test]
    public async Task ForwardedRecords_JoinTheCentralChain_OnceEach()
    {
        var store = new InMemoryDataAccess<AuditRecord>();
        var central = new AuditLog(store, TimeProvider.System, integrity: Keyed);
        var forwarded = new AuditRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow, Actor = "core-runner", Action = "workflow.egress.denied",
            Subject = "run-1", Outcome = "exfil.example:443", ChainId = "runner-chain", Sequence = 7, Hash = "x"
        };

        await central.AppendForwardedAsync(forwarded, "core-runner:abc");
        await central.AppendForwardedAsync(forwarded, "core-runner:abc");

        var stored = (await AllAsync(store)).Single();
        Assert.That(stored.Id, Is.EqualTo(forwarded.Id));
        Assert.That(stored.Source, Is.EqualTo("core-runner:abc"));
        Assert.That(stored.ChainId, Is.Not.EqualTo("runner-chain"), "re-chained into the central trail");
        Assert.That(AuditChain.Verify(await AllAsync(store), Keyed).Intact, Is.True);
    }

    private sealed class RecordingRelay : IAuditRelay
    {
        public List<AuditRecord> Relayed { get; } = [];

        public Task RelayAsync(AuditRecord record, CancellationToken ct)
        {
            Relayed.Add(record);
            return Task.CompletedTask;
        }
    }

    [Test]
    public async Task EveryLocalRecord_IsRelayedToTheCentralAuthority_ForwardedOnesAreNot()
    {
        var relay = new RecordingRelay();
        var log = new AuditLog(new InMemoryDataAccess<AuditRecord>(), TimeProvider.System, relay: relay);

        await log.AppendAsync("svc", "a", "s", "ok");
        await log.AppendForwardedAsync(new AuditRecord
        {
            TimestampUtc = DateTimeOffset.UtcNow, Actor = "x", Action = "b", Subject = "s", Outcome = "ok"
        }, "elsewhere");

        Assert.That(relay.Relayed.Select(r => r.Action), Is.EqualTo(new[] { "a" }));
    }
}
