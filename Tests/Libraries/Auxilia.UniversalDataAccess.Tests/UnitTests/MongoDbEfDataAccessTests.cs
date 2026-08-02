using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.UniversalDataAccess.Settings;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Auxilia.UniversalDataAccess.Tests.UnitTests;

/// <summary>
/// Simple, scalar-only entity used so that the EF Core InMemory provider needs no
/// complex type or owned-entity configuration.
/// </summary>
public sealed class MongoUnitTestEntity : IEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

[TestFixture]
[Category("Unit")]
public class MongoDbEfDataAccessTests
{
    private DbContextOptions<MongoDbContext<MongoUnitTestEntity>> _inMemoryOptions = null!;
    private MongoDbSettings<MongoUnitTestEntity> _settings = null!;
    private MongoDbEfDataAccess<MongoUnitTestEntity> _sut = null!;

    [SetUp]
    public void SetUp()
    {
        // Each test gets its own isolated InMemory database
        _inMemoryOptions = new DbContextOptionsBuilder<MongoDbContext<MongoUnitTestEntity>>()
            .UseInMemoryDatabase($"mongo-unit-{Guid.NewGuid()}")
            .Options;

        _settings = new MongoDbSettings<MongoUnitTestEntity>();

        var mockFactory = new Mock<IDbContextFactory<MongoDbContext<MongoUnitTestEntity>>>();
        mockFactory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MongoDbContext<MongoUnitTestEntity>(_inMemoryOptions, _settings));

        _sut = new MongoDbEfDataAccess<MongoUnitTestEntity>(mockFactory.Object);
    }

    [TearDown]
    public void TearDown() => _sut.Dispose();

    // ── Helpers ────────────────────────────────────────────────────────────

    private static MongoUnitTestEntity NewEntity(string name = "Test") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Count = 7,
        IsActive = true,
        CreatedAt = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc)
    };

    // ── Read ───────────────────────────────────────────────────────────────

    [Test]
    public async Task ReadAsync_WhenEmpty_ReturnsEmptyQueryable()
    {
        var result = await _sut.ReadAsync();
        Assert.That(result.ToList(), Is.Empty);
    }

    [Test]
    public async Task ReadAsync_ById_WhenNotFound_ReturnsNull()
    {
        var result = await _sut.ReadAsync(Guid.NewGuid());
        Assert.That(result, Is.Null);
    }

    // ── Save (Add) ─────────────────────────────────────────────────────────

    [Test]
    public async Task SaveAsync_NewEntity_ReturnsFalse()
    {
        var wasUpdated = await _sut.SaveAsync(NewEntity());
        Assert.That(wasUpdated, Is.False);
    }

    [Test]
    public async Task SaveAsync_NewEntity_CanBeReadBackById()
    {
        var entity = NewEntity("ReadById");
        await _sut.SaveAsync(entity);

        var readBack = await _sut.ReadAsync(entity.Id);

        Assert.That(readBack, Is.Not.Null);
        Assert.That(readBack!.Id, Is.EqualTo(entity.Id));
        Assert.That(readBack.Name, Is.EqualTo(entity.Name));
        Assert.That(readBack.Count, Is.EqualTo(entity.Count));
        Assert.That(readBack.IsActive, Is.EqualTo(entity.IsActive));
        Assert.That(readBack.CreatedAt, Is.EqualTo(entity.CreatedAt));
    }

    [Test]
    public async Task SaveAsync_NewEntity_AppearsInQueryable()
    {
        var entity = NewEntity();
        await _sut.SaveAsync(entity);

        var all = (await _sut.ReadAsync()).ToList();

        Assert.That(all, Has.Count.EqualTo(1));
        Assert.That(all[0].Id, Is.EqualTo(entity.Id));
    }

    // ── Save (Update) ──────────────────────────────────────────────────────

    [Test]
    public async Task SaveAsync_ExistingEntity_ReturnsTrue()
    {
        var entity = NewEntity();
        await _sut.SaveAsync(entity);

        entity.Name = "Updated";
        var wasUpdated = await _sut.SaveAsync(entity);
        Assert.That(wasUpdated, Is.True);
    }

    [Test]
    public async Task SaveAsync_ExistingEntity_UpdatesStoredValues()
    {
        var entity = NewEntity("Original");
        await _sut.SaveAsync(entity);

        entity.Name = "Updated";
        entity.Count = 99;
        await _sut.SaveAsync(entity);

        var readBack = await _sut.ReadAsync(entity.Id);
        Assert.That(readBack!.Name, Is.EqualTo("Updated"));
        Assert.That(readBack.Count, Is.EqualTo(99));
    }

    // ── Remove ─────────────────────────────────────────────────────────────

    [Test]
    public async Task RemoveAsync_NonExistentId_ReturnsFalse()
    {
        Assert.That(await _sut.RemoveAsync(Guid.NewGuid()), Is.False);
    }

    [Test]
    public async Task RemoveAsync_ExistingEntity_ReturnsTrue()
    {
        var entity = NewEntity();
        await _sut.SaveAsync(entity);

        Assert.That(await _sut.RemoveAsync(entity.Id), Is.True);
    }

    [Test]
    public async Task RemoveAsync_ExistingEntity_IsNoLongerReadable()
    {
        var entity = NewEntity();
        await _sut.SaveAsync(entity);
        await _sut.RemoveAsync(entity.Id);

        Assert.That(await _sut.ReadAsync(entity.Id), Is.Null);
    }

    [Test]
    public async Task RemoveAsync_ExistingEntity_NoLongerAppearsInQueryable()
    {
        var entity = NewEntity();
        await _sut.SaveAsync(entity);
        await _sut.RemoveAsync(entity.Id);

        var all = (await _sut.ReadAsync()).ToList();
        Assert.That(all, Is.Empty);
    }

    // ── Cancellation ───────────────────────────────────────────────────────

    [Test]
    public void ReadAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var ct = new CancellationToken(canceled: true);
        Assert.ThrowsAsync<OperationCanceledException>(() => _sut.ReadAsync(ct));
    }

    [Test]
    public void ReadAsync_ById_CancelledToken_ThrowsOperationCanceled()
    {
        var ct = new CancellationToken(canceled: true);
        Assert.ThrowsAsync<OperationCanceledException>(() => _sut.ReadAsync(Guid.NewGuid(), ct));
    }

    [Test]
    public void SaveAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var ct = new CancellationToken(canceled: true);
        Assert.ThrowsAsync<OperationCanceledException>(() => _sut.SaveAsync(NewEntity(), ct));
    }

    [Test]
    public void RemoveAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var ct = new CancellationToken(canceled: true);
        Assert.ThrowsAsync<OperationCanceledException>(() => _sut.RemoveAsync(Guid.NewGuid(), ct));
    }

    // ── Observables ────────────────────────────────────────────────────────

    [Test]
    public async Task EntityAdded_FiresOnNewSave()
    {
        MongoUnitTestEntity? received = null;
        _sut.EntityAdded.Subscribe(e => received = e);

        var entity = NewEntity("Observable");
        await _sut.SaveAsync(entity);

        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Id, Is.EqualTo(entity.Id));
    }

    [Test]
    public async Task EntityAdded_DoesNotFireOnUpdate()
    {
        var entity = NewEntity();
        await _sut.SaveAsync(entity);

        var addCount = 0;
        _sut.EntityAdded.Subscribe(_ => addCount++);

        entity.Name = "Changed";
        await _sut.SaveAsync(entity);

        Assert.That(addCount, Is.EqualTo(0));
    }

    [Test]
    public async Task EntityUpdated_FiresOnExistingSave()
    {
        var entity = NewEntity();
        await _sut.SaveAsync(entity);

        MongoUnitTestEntity? received = null;
        _sut.EntityUpdated.Subscribe(e => received = e);

        entity.Name = "Changed";
        await _sut.SaveAsync(entity);

        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Name, Is.EqualTo("Changed"));
    }

    [Test]
    public async Task EntityRemoved_FiresWithRemovedEntity()
    {
        var entity = NewEntity();
        await _sut.SaveAsync(entity);

        MongoUnitTestEntity? received = null;
        _sut.EntityRemoved.Subscribe(e => received = e);

        await _sut.RemoveAsync(entity.Id);

        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Id, Is.EqualTo(entity.Id));
    }

    [Test]
    public async Task EntityRemoved_DoesNotFireWhenNothingRemoved()
    {
        var count = 0;
        _sut.EntityRemoved.Subscribe(_ => count++);

        await _sut.RemoveAsync(Guid.NewGuid());

        Assert.That(count, Is.EqualTo(0));
    }

    // ── Multiple entities ──────────────────────────────────────────────────

    [Test]
    public async Task ReadAsync_ReturnsAllSavedEntities()
    {
        await _sut.SaveAsync(NewEntity("A"));
        await _sut.SaveAsync(NewEntity("B"));
        await _sut.SaveAsync(NewEntity("C"));

        var all = (await _sut.ReadAsync()).ToList();
        Assert.That(all, Has.Count.EqualTo(3));
    }
}

