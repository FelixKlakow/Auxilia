using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.UniversalDataAccess.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace Auxilia.UniversalDataAccess.Tests.UnitTests;

/// <summary>
/// Write-race behaviour of the Mongo-shaped backend for versioned entities: lost races retry a
/// bounded number of times, real write errors surface immediately, and a removal that races a
/// concurrent writer still reports the documented result. The EF InMemory provider honours the
/// Version concurrency token exactly like the filtered replace on MongoDB, so a save-changes
/// interceptor that mutates the store out-of-band reproduces every race deterministically.
/// </summary>
[TestFixture]
[Category("Unit")]
public class MongoDbEfDataAccessWriteRaceTests
{
    /// <summary>Runs a hook before each SaveChanges; the hook may throw to simulate a store error.</summary>
    private sealed class SavingHook(Func<int, Task> onSaving) : SaveChangesInterceptor
    {
        public int Calls { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Calls++;
            await onSaving(Calls);
            return result;
        }
    }

    private string _databaseName = null!;
    private MongoDbSettings<VersionedTestEntity> _settings = null!;
    private readonly List<MongoDbEfDataAccess<VersionedTestEntity>> _created = [];

    [SetUp]
    public void SetUp()
    {
        _databaseName = $"mongo-race-{Guid.NewGuid()}";
        _settings = new MongoDbSettings<VersionedTestEntity>();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var sut in _created)
            sut.Dispose();
        _created.Clear();
    }

    private DbContextOptions<MongoDbContext<VersionedTestEntity>> Options(SavingHook? hook = null)
    {
        var builder = new DbContextOptionsBuilder<MongoDbContext<VersionedTestEntity>>()
            .UseInMemoryDatabase(_databaseName);
        if (hook is not null)
            builder.AddInterceptors(hook);
        return builder.Options;
    }

    private MongoDbEfDataAccess<VersionedTestEntity> CreateSut(SavingHook? hook = null)
    {
        var options = Options(hook);
        var factory = new Mock<IDbContextFactory<MongoDbContext<VersionedTestEntity>>>();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MongoDbContext<VersionedTestEntity>(options, _settings));
        var sut = new MongoDbEfDataAccess<VersionedTestEntity>(factory.Object);
        _created.Add(sut);
        return sut;
    }

    /// <summary>A second writer sharing the same database but not the hook (so it never recurses).</summary>
    private MongoDbEfDataAccess<VersionedTestEntity> OtherWriter() => CreateSut();

    private static CancellationToken TestToken => TestContext.CurrentContext.CancellationToken;

    // ── SaveAsync (versioned) ───────────────────────────────────────────────

    [Test, CancelAfter(10_000)]
    public async Task SaveAsync_Versioned_NonConcurrencyWriteError_SurfacesWithoutRetrying()
    {
        var hook = new SavingHook(_ => throw new DbUpdateException("write concern failed"));
        var sut = CreateSut(hook);

        Assert.ThrowsAsync<DbUpdateException>(
            () => sut.SaveAsync(new VersionedTestEntity { Name = "v1" }, TestToken));
        Assert.That(hook.Calls, Is.EqualTo(1), "A non-concurrency write error must not be retried.");
    }

    [Test, CancelAfter(10_000)]
    public async Task SaveAsync_Versioned_PersistentConcurrencyConflict_StopsAfterBoundedRetries()
    {
        var hook = new SavingHook(_ => throw new DbUpdateConcurrencyException("version moved"));
        var sut = CreateSut(hook);

        Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => sut.SaveAsync(new VersionedTestEntity { Name = "v1" }, TestToken));
        Assert.That(hook.Calls, Is.EqualTo(8), "Lost races retry a bounded number of times, then surface.");
    }

    [Test, CancelAfter(10_000)]
    public async Task SaveAsync_Versioned_LostRace_RetriesAndWins()
    {
        var entity = new VersionedTestEntity { Name = "v1" };
        await OtherWriter().SaveAsync(entity, TestToken); // stored version 1

        var other = OtherWriter();
        var hook = new SavingHook(async call =>
        {
            // Between this writer's read and its write, another writer bumps the version.
            if (call == 1)
                await other.SaveAsync(new VersionedTestEntity { Id = entity.Id, Name = "interloper" }, TestToken);
        });
        var sut = CreateSut(hook);

        var updated = await sut.SaveAsync(new VersionedTestEntity { Id = entity.Id, Name = "mine" }, TestToken);

        Assert.That(updated, Is.True);
        Assert.That(hook.Calls, Is.EqualTo(2), "The lost race is retried exactly once.");
        var stored = await sut.ReadAsync(entity.Id, TestToken);
        Assert.That(stored!.Name, Is.EqualTo("mine"), "Last writer wins.");
        Assert.That(stored.Version, Is.EqualTo(3), "Every write, including the interloper's, advanced the version.");
    }

    [Test, CancelAfter(10_000)]
    public async Task SaveAsync_Versioned_ConcurrentInsertOfSameId_RetriesAsUpdate()
    {
        var id = Guid.NewGuid();
        var other = OtherWriter();
        var hook = new SavingHook(async call =>
        {
            // The interloper lands first; the store then rejects this insert as a duplicate key
            // (the InMemory provider throws a bare ArgumentException there, so the rejection is
            // raised here the way MongoDB surfaces it).
            if (call != 1)
                return;
            await other.SaveAsync(new VersionedTestEntity { Id = id, Name = "first" }, TestToken);
            throw new DbUpdateException("E11000 duplicate key");
        });
        var sut = CreateSut(hook);

        var updated = await sut.SaveAsync(new VersionedTestEntity { Id = id, Name = "second" }, TestToken);

        Assert.That(updated, Is.True, "The retry sees the interloper's row and updates it.");
        Assert.That(hook.Calls, Is.EqualTo(2));
        var stored = await sut.ReadAsync(id, TestToken);
        Assert.That(stored!.Name, Is.EqualTo("second"));
        Assert.That(stored.Version, Is.EqualTo(2));
    }

    // ── RemoveAsync (versioned) ─────────────────────────────────────────────

    [Test, CancelAfter(10_000)]
    public async Task RemoveAsync_Versioned_ConcurrentlyRemoved_ReturnsFalse()
    {
        var entity = new VersionedTestEntity { Name = "v1" };
        await OtherWriter().SaveAsync(entity, TestToken);

        var other = OtherWriter();
        var hook = new SavingHook(async call =>
        {
            if (call == 1)
                await other.RemoveAsync(entity.Id, TestToken);
        });
        var sut = CreateSut(hook);
        var removedEvents = 0;
        sut.EntityRemoved.Subscribe(_ => removedEvents++);

        var removed = await sut.RemoveAsync(entity.Id, TestToken);

        Assert.That(removed, Is.False, "Someone else removed it first — nothing was deleted by this call.");
        Assert.That(removedEvents, Is.EqualTo(0));
    }

    [Test, CancelAfter(10_000)]
    public async Task RemoveAsync_Versioned_ConcurrentlyUpdated_StillRemoves()
    {
        var entity = new VersionedTestEntity { Name = "v1" };
        await OtherWriter().SaveAsync(entity, TestToken);

        var other = OtherWriter();
        var hook = new SavingHook(async call =>
        {
            if (call == 1)
                await other.SaveAsync(new VersionedTestEntity { Id = entity.Id, Name = "moved" }, TestToken);
        });
        var sut = CreateSut(hook);

        var removed = await sut.RemoveAsync(entity.Id, TestToken);

        Assert.That(removed, Is.True, "Removal is by id: a version bump in between is re-read and the row still goes.");
        Assert.That(await sut.ReadAsync(entity.Id, TestToken), Is.Null);
    }

    [Test, CancelAfter(10_000)]
    public async Task RemoveAsync_Versioned_PersistentConcurrencyConflict_StopsAfterBoundedRetries()
    {
        var entity = new VersionedTestEntity { Name = "v1" };
        await OtherWriter().SaveAsync(entity, TestToken);

        var hook = new SavingHook(_ => throw new DbUpdateConcurrencyException("version moved"));
        var sut = CreateSut(hook);

        Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => sut.RemoveAsync(entity.Id, TestToken));
        Assert.That(hook.Calls, Is.EqualTo(8));
    }
}
