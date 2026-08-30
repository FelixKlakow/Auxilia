using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.UniversalDataAccess.Settings;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Auxilia.UniversalDataAccess.Tests.UnitTests;

/// <summary>Versioned entity for the compare-and-swap contract tests (class, not record, so the
/// EF Core InMemory provider can materialize it for the Mongo-shaped backend).</summary>
public sealed class VersionedTestEntity : IVersionedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public long Version { get; set; }
}

/// <summary>
/// The conditional-save (compare-and-swap) contract every backend must honor: a save conditioned
/// on the version observed at read time wins at most once; stale versions and vanished rows are
/// rejected; plain saves stay last-writer-wins but still advance the version.
/// </summary>
[Category("Unit")]
public abstract class ConditionalSaveContractTests
{
    protected abstract IDataAccess<VersionedTestEntity> Store { get; }

    private static VersionedTestEntity NewEntity(string name = "v1") => new() { Name = name };

    [Test]
    public async Task TrySaveAsync_NewEntity_ExpectingAbsence_SucceedsAndStampsVersionOne()
    {
        var entity = NewEntity();

        var won = await Store.TrySaveAsync(entity, expectedVersion: 0);

        Assert.That(won, Is.True);
        var readBack = await Store.ReadAsync(entity.Id);
        Assert.That(readBack!.Version, Is.EqualTo(1));
    }

    [Test]
    public async Task TrySaveAsync_NewEntity_ExpectingAVersion_IsRejected()
    {
        var entity = NewEntity();

        var won = await Store.TrySaveAsync(entity, expectedVersion: 3);

        Assert.That(won, Is.False);
        Assert.That(await Store.ReadAsync(entity.Id), Is.Null,
            "A rejected conditional save must not store anything.");
    }

    [Test]
    public async Task TrySaveAsync_MatchingVersion_SucceedsAndBumps()
    {
        var entity = NewEntity();
        await Store.SaveAsync(entity); // version 1

        var update = new VersionedTestEntity { Id = entity.Id, Name = "updated" };
        var won = await Store.TrySaveAsync(update, expectedVersion: 1);

        Assert.That(won, Is.True);
        var readBack = await Store.ReadAsync(entity.Id);
        Assert.That(readBack!.Name, Is.EqualTo("updated"));
        Assert.That(readBack.Version, Is.EqualTo(2));
    }

    [Test]
    public async Task TrySaveAsync_StaleVersion_IsRejected_AndTheWinnerStands()
    {
        var entity = NewEntity();
        await Store.SaveAsync(entity); // version 1

        // Two writers both read version 1; the first swap wins, the second must lose.
        var first = new VersionedTestEntity { Id = entity.Id, Name = "winner" };
        var second = new VersionedTestEntity { Id = entity.Id, Name = "loser" };
        Assert.That(await Store.TrySaveAsync(first, expectedVersion: 1), Is.True);

        var won = await Store.TrySaveAsync(second, expectedVersion: 1);

        Assert.That(won, Is.False);
        var readBack = await Store.ReadAsync(entity.Id);
        Assert.That(readBack!.Name, Is.EqualTo("winner"));
        Assert.That(readBack.Version, Is.EqualTo(2));
    }

    [Test]
    public async Task TrySaveAsync_AfterRemoval_IsRejected()
    {
        var entity = NewEntity();
        await Store.SaveAsync(entity); // version 1
        await Store.RemoveAsync(entity.Id);

        var won = await Store.TrySaveAsync(
            new VersionedTestEntity { Id = entity.Id, Name = "ghost" }, expectedVersion: 1);

        Assert.That(won, Is.False);
        Assert.That(await Store.ReadAsync(entity.Id), Is.Null,
            "A conditional save must never resurrect a removed entity.");
    }

    [Test]
    public async Task SaveAsync_LastWriterWins_ButStillAdvancesTheVersion()
    {
        var entity = NewEntity();
        await Store.SaveAsync(entity);
        Assert.That(entity.Version, Is.EqualTo(1));

        // A plain save with a stale in-memory version still wins (LWW) — but bumps the stored one.
        var stale = new VersionedTestEntity { Id = entity.Id, Name = "lww" };
        await Store.SaveAsync(stale);

        var readBack = await Store.ReadAsync(entity.Id);
        Assert.That(readBack!.Name, Is.EqualTo("lww"));
        Assert.That(readBack.Version, Is.EqualTo(2),
            "Every write must advance the version so concurrent conditional saves observe it.");
    }
}

[TestFixture]
[Category("Unit")]
public sealed class InMemoryConditionalSaveTests : ConditionalSaveContractTests
{
    private InMemoryDataAccess<VersionedTestEntity> _store = null!;
    private InMemoryDataAccess<TestEntity> _unversioned = null!;

    protected override IDataAccess<VersionedTestEntity> Store => _store;

    [SetUp]
    public void SetUp()
    {
        _store = new InMemoryDataAccess<VersionedTestEntity>();
        _unversioned = new InMemoryDataAccess<TestEntity>();
    }

    [TearDown]
    public void TearDown()
    {
        _store.Dispose();
        _unversioned.Dispose();
    }

    [Test]
    public void TrySaveAsync_OnAnUnversionedEntity_Throws()
    {
        Assert.ThrowsAsync<NotSupportedException>(
            () => ((IDataAccess<TestEntity>)_unversioned).TrySaveAsync(TestEntity.CreateSample(), 0));
    }

    [Test]
    public async Task TrySaveAsync_ConcurrentSwapsOnTheSameVersion_ExactlyOneWins()
    {
        var entity = new VersionedTestEntity { Name = "base" };
        await _store.SaveAsync(entity); // version 1

        var attempts = Enumerable.Range(0, 16)
            .Select(i => Task.Run(() => _store.TrySaveAsync(
                new VersionedTestEntity { Id = entity.Id, Name = $"writer-{i}" }, expectedVersion: 1)))
            .ToList();
        var results = await Task.WhenAll(attempts);

        Assert.That(results.Count(r => r), Is.EqualTo(1),
            "Concurrent conditional saves against the same observed version must admit exactly one winner.");
        Assert.That((await _store.ReadAsync(entity.Id))!.Version, Is.EqualTo(2));
    }
}

[TestFixture]
[Category("Unit")]
public sealed class JsonFileConditionalSaveTests : ConditionalSaveContractTests
{
    private static readonly string TestDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Auxilia", "TestData", "ConditionalSave");

    private string _testFilePath = null!;
    private JsonFileDataAccess<VersionedTestEntity> _store = null!;

    protected override IDataAccess<VersionedTestEntity> Store => _store;

    [SetUp]
    public void SetUp()
    {
        Directory.CreateDirectory(TestDataDir);
        _testFilePath = Path.Combine(TestDataDir, $"{Guid.NewGuid()}.json");
        _store = new JsonFileDataAccess<VersionedTestEntity>(
            new JsonStorageSettings<VersionedTestEntity> { StorageFilePath = _testFilePath });
    }

    [TearDown]
    public void TearDown()
    {
        _store.Dispose();
        if (File.Exists(_testFilePath))
            File.Delete(_testFilePath);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (Directory.Exists(TestDataDir))
            Directory.Delete(TestDataDir, recursive: true);
    }

    [Test]
    public async Task Version_SurvivesAReload_SoAStaleSwapStillLoses()
    {
        var entity = new VersionedTestEntity { Name = "persisted" };
        await _store.SaveAsync(entity); // version 1, persisted
        _store.Dispose();

        _store = new JsonFileDataAccess<VersionedTestEntity>(
            new JsonStorageSettings<VersionedTestEntity> { StorageFilePath = _testFilePath });
        Assert.That(await _store.TrySaveAsync(
            new VersionedTestEntity { Id = entity.Id, Name = "next" }, expectedVersion: 1), Is.True);
        Assert.That(await _store.TrySaveAsync(
            new VersionedTestEntity { Id = entity.Id, Name = "stale" }, expectedVersion: 1), Is.False);
    }
}

[TestFixture]
[Category("Unit")]
public sealed class MongoDbEfConditionalSaveTests : ConditionalSaveContractTests
{
    private MongoDbEfDataAccess<VersionedTestEntity> _store = null!;

    protected override IDataAccess<VersionedTestEntity> Store => _store;

    [SetUp]
    public void SetUp()
    {
        // Same harness as MongoDbEfDataAccessTests: an isolated EF InMemory database per test.
        // The Version concurrency-token mapping in MongoDbContext is enforced by this provider
        // too, so the filtered-replace losing path is exercised, not just the pre-check.
        var options = new DbContextOptionsBuilder<MongoDbContext<VersionedTestEntity>>()
            .UseInMemoryDatabase($"mongo-cas-{Guid.NewGuid()}")
            .Options;
        var settings = new MongoDbSettings<VersionedTestEntity>();
        var factory = new Mock<IDbContextFactory<MongoDbContext<VersionedTestEntity>>>();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MongoDbContext<VersionedTestEntity>(options, settings));
        _store = new MongoDbEfDataAccess<VersionedTestEntity>(factory.Object);
    }

    [TearDown]
    public void TearDown() => _store.Dispose();
}
