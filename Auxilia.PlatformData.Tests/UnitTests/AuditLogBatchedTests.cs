using Auxilia.PlatformData;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.PlatformData.Tests.UnitTests;

/// <summary>
/// The opt-in batched audit writer: appends enqueue and one background writer drains — with
/// full-order preservation, a working flush barrier, and a draining shutdown. The default
/// (unbatched) path stays write-through.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class AuditLogBatchedTests
{
    private InMemoryDataAccess<AuditRecord> _store = null!;

    [SetUp]
    public void SetUp() => _store = new InMemoryDataAccess<AuditRecord>();

    [TearDown]
    public void TearDown() => _store.Dispose();

    [Test]
    public async Task Default_IsWriteThrough()
    {
        await using var log = new AuditLog(_store, TimeProvider.System);
        await log.AppendAsync("actor", "action", "subject", "outcome");
        Assert.That((await _store.ReadAsync()).Count(), Is.EqualTo(1),
            "without batching, the append IS the store write");
    }

    [Test]
    public async Task Batched_FlushBarrier_GuaranteesEverythingBefore_IsPersisted()
    {
        await using var log = new AuditLog(_store, TimeProvider.System,
            new AuditLogSettings { BatchedWrites = true });

        for (var i = 0; i < 200; i++)
            await log.AppendAsync("actor", "action", $"subject-{i}", "ok");
        await log.FlushAsync();

        var records = (await _store.ReadAsync()).ToList();
        Assert.That(records, Has.Count.EqualTo(200));
    }

    [Test]
    public async Task Batched_PreservesAppendOrder()
    {
        await using var log = new AuditLog(_store, TimeProvider.System,
            new AuditLogSettings { BatchedWrites = true });

        for (var i = 0; i < 50; i++)
            await log.AppendAsync("actor", "action", i.ToString("D3"), "ok");
        await log.FlushAsync();

        var subjects = (await _store.ReadAsync())
            .OrderBy(r => r.TimestampUtc).Select(r => r.Subject).ToList();
        // The single-reader drain must not reorder; subjects were appended in order.
        Assert.That(subjects, Is.EqualTo(subjects.OrderBy(s => s, StringComparer.Ordinal).ToList()));
    }

    [Test]
    public async Task Batched_ConcurrentAppenders_LoseNothing()
    {
        await using var log = new AuditLog(_store, TimeProvider.System,
            new AuditLogSettings { BatchedWrites = true, MaxQueued = 64 }); // small bound → real backpressure

        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
                await log.AppendAsync($"writer-{writer}", "action", $"{writer}/{i}", "ok");
        })));
        await log.FlushAsync();

        var records = (await _store.ReadAsync()).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(records, Has.Count.EqualTo(800),
                "backpressure may slow appenders but must never drop audit");
            Assert.That(records.Select(r => r.Subject).Distinct().Count(), Is.EqualTo(800));
        });
    }

    [Test]
    public async Task Batched_DisposeDrains_TheQueuedTail()
    {
        var log = new AuditLog(_store, TimeProvider.System,
            new AuditLogSettings { BatchedWrites = true });
        for (var i = 0; i < 100; i++)
            await log.AppendAsync("actor", "action", $"tail-{i}", "ok");

        await log.DisposeAsync(); // orderly shutdown — no flush call

        Assert.That((await _store.ReadAsync()).Count(), Is.EqualTo(100),
            "an orderly shutdown must drain everything queued");
    }
}
