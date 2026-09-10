using Auxilia.Core.Api.Data;
using Auxilia.Core.Api.Services;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.Workflows.Messaging.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Api.Tests.UnitTests;

/// <summary>
/// Unit tests for the view mirror: the per-run cap is enforced from a counter seeded ONCE from
/// the store (never a full scan per message), re-deliveries do not consume cap, and the
/// retention sweep removes only the rows of runs that have been terminal longer than
/// <see cref="CoreApiSettings.ViewRetentionDays"/>.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class RunViewTrackingServiceTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private FakeMessageBusClient _bus = null!;
    private CountingDataAccess<CoreRunViewRecord> _views = null!;
    private IDataAccess<CoreRunRecord> _runs = null!;
    private ManualTimeProvider _time = null!;
    private RunViewTrackingService _sut = null!;

    [SetUp]
    public async Task SetUp()
    {
        _bus = new FakeMessageBusClient();
        _views = new CountingDataAccess<CoreRunViewRecord>(new InMemoryDataAccess<CoreRunViewRecord>());
        _runs = new InMemoryDataAccess<CoreRunRecord>();
        _time = new ManualTimeProvider();
        _sut = new RunViewTrackingService(_bus, _views, _runs,
            Options.Create(new CoreApiSettings { MaxPersistedViewItemsPerRun = 2, ViewRetentionDays = 7 }),
            _time, NullLogger<RunViewTrackingService>.Instance);
        await _sut.StartAsync(CancellationToken.None);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _sut.StopAsync(CancellationToken.None);
        _sut.Dispose();
        (_runs as IDisposable)?.Dispose();
    }

    private Task DeliverAsync(Guid runId, long sequence, string view = "log")
        => _bus.SimulateReceivedAsync(ViewDataMessage.ExchangeName,
            new ViewDataMessage(runId, view, sequence, """{"line":"x"}"""));

    private async Task<int> StoredForAsync(Guid runId)
        => (await _views.ReadAsync(CancellationToken.None)).Count(v => v.RunId == runId);

    [Test]
    public async Task Cap_IsEnforcedFromACounterSeededOnce_NotAFullScanPerMessage()
    {
        var runId = Guid.NewGuid();
        var scansBefore = _views.FullReads; // the start-up retention sweep has already scanned

        await DeliverAsync(runId, 1);
        await DeliverAsync(runId, 2);
        await DeliverAsync(runId, 3);
        var scansForThreeMessages = _views.FullReads - scansBefore;

        Assert.Multiple(async () =>
        {
            Assert.That(await StoredForAsync(runId), Is.EqualTo(2), "items beyond the cap are dropped");
            Assert.That(scansForThreeMessages, Is.EqualTo(1),
                "the store is scanned once to seed the run's counter, never per message");
        });
    }

    [Test]
    public async Task Redelivery_UpsertsInPlace_AndDoesNotConsumeCap()
    {
        var runId = Guid.NewGuid();

        await DeliverAsync(runId, 1);
        await DeliverAsync(runId, 1);
        await DeliverAsync(runId, 2);

        Assert.That(await StoredForAsync(runId), Is.EqualTo(2), "seq 1 twice + seq 2 = two rows, both kept");
    }

    [Test]
    public async Task Sweep_RemovesViewsOfRunsTerminalPastRetention_AndKeepsTheRest()
    {
        var oldTerminal = await SeedRunAsync("Success", _time.Now.AddDays(-10));
        var recentTerminal = await SeedRunAsync("Failed", _time.Now.AddDays(-1));
        var oldLive = await SeedRunAsync("Running", _time.Now.AddDays(-10));
        var orphan = Guid.NewGuid();
        foreach (var runId in new[] { oldTerminal, recentTerminal, oldLive })
            await DeliverAsync(runId, 1);
        await _views.SaveAsync(new CoreRunViewRecord
        {
            Id = CoreRunViewRecord.IdFor(orphan, "log", 1), RunId = orphan, ViewName = "log", Sequence = 1,
            PayloadJson = "{}", TimestampUtc = _time.Now.AddDays(-30)
        }, CancellationToken.None);

        await _sut.SweepOnceAsync();

        Assert.Multiple(async () =>
        {
            Assert.That(await StoredForAsync(oldTerminal), Is.Zero, "terminal for 10 days > 7-day retention");
            Assert.That(await StoredForAsync(orphan), Is.Zero, "an old row without a run record is garbage");
            Assert.That(await StoredForAsync(recentTerminal), Is.EqualTo(1), "terminal only yesterday");
            Assert.That(await StoredForAsync(oldLive), Is.EqualTo(1), "a live run's views are never swept");
        });
    }

    private async Task<Guid> SeedRunAsync(string state, DateTimeOffset updatedUtc)
    {
        var id = Guid.NewGuid();
        await _runs.SaveAsync(new CoreRunRecord
        {
            Id = id, WorkflowType = "wf", State = state, CreatedUtc = updatedUtc, UpdatedUtc = updatedUtc
        }, CancellationToken.None);
        return id;
    }

    /// <summary>Delegating store that counts full-collection reads (the scan the cap must not do per message).</summary>
    private sealed class CountingDataAccess<T>(IDataAccess<T> inner) : IDataAccess<T> where T : IEntity
    {
        private int _fullReads;

        public int FullReads => Volatile.Read(ref _fullReads);

        public IObservable<T> EntityAdded => inner.EntityAdded;
        public IObservable<T> EntityUpdated => inner.EntityUpdated;
        public IObservable<T> EntityRemoved => inner.EntityRemoved;

        public Task<IQueryable<T>> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _fullReads);
            return inner.ReadAsync(cancellationToken);
        }

        public Task<T?> ReadAsync(Guid id, CancellationToken cancellationToken) => inner.ReadAsync(id, cancellationToken);
        public Task<bool> SaveAsync(T entity, CancellationToken cancellationToken) => inner.SaveAsync(entity, cancellationToken);
        public Task<bool> TrySaveAsync(T entity, long expectedVersion, CancellationToken cancellationToken)
            => inner.TrySaveAsync(entity, expectedVersion, cancellationToken);
        public Task<bool> RemoveAsync(Guid id) => inner.RemoveAsync(id);
        public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken) => inner.RemoveAsync(id, cancellationToken);
    }
}
