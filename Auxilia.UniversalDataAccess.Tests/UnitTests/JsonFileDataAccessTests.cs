using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.UniversalDataAccess.Settings;

namespace Auxilia.UniversalDataAccess.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class JsonFileDataAccessTests
{
    private static readonly string TestDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Auxilia", "TestData", "DataAccess");

    private string _testFilePath = null!;
    private JsonStorageSettings<TestEntity> _settings = null!;
    private JsonFileDataAccess<TestEntity> _sut = null!;

    [SetUp]
    public void SetUp()
    {
        Directory.CreateDirectory(TestDataDir);
        _testFilePath = Path.Combine(TestDataDir, $"{Guid.NewGuid()}.json");
        _settings = new JsonStorageSettings<TestEntity> { StorageFilePath = _testFilePath };
        _sut = new JsonFileDataAccess<TestEntity>(_settings);
    }

    [TearDown]
    public void TearDown()
    {
        _sut.Dispose();
        if (File.Exists(_testFilePath))
            File.Delete(_testFilePath);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        if (Directory.Exists(TestDataDir))
            Directory.Delete(TestDataDir, recursive: true);
    }

    // ── Read ──────────────────────────────────────────────────────────────

    [Test]
    public async Task ReadAsync_WhenFileDoesNotExist_ReturnsEmptyQueryable()
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

    // ── Save (Add) ────────────────────────────────────────────────────────

    [Test]
    public async Task SaveAsync_NewEntity_ReturnsFalse()
    {
        var entity = TestEntity.CreateSample();
        var wasUpdated = await _sut.SaveAsync(entity);
        Assert.That(wasUpdated, Is.False);
    }

    [Test]
    public async Task SaveAsync_NewEntity_CanBeReadBack()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);

        var readBack = await _sut.ReadAsync(entity.Id);

        Assert.That(readBack, Is.Not.Null);
        entity.AssertEquivalentTo(readBack!);
    }

    [Test]
    public async Task SaveAsync_NewEntity_AppearsInQueryable()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);

        var all = (await _sut.ReadAsync()).ToList();

        Assert.That(all, Has.Count.EqualTo(1));
        entity.AssertEquivalentTo(all[0]);
    }

    // ── Save (Update) ─────────────────────────────────────────────────────

    [Test]
    public async Task SaveAsync_ExistingEntity_ReturnsTrue()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);

        var wasUpdated = await _sut.SaveAsync(entity with { Name = "Updated" });
        Assert.That(wasUpdated, Is.True);
    }

    [Test]
    public async Task SaveAsync_ExistingEntity_UpdatesStoredValues()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);

        var updated = entity with { Name = "Updated", Count = 99 };
        await _sut.SaveAsync(updated);

        var readBack = await _sut.ReadAsync(entity.Id);
        Assert.That(readBack!.Name, Is.EqualTo("Updated"));
        Assert.That(readBack.Count, Is.EqualTo(99));
    }

    // ── Remove ────────────────────────────────────────────────────────────

    [Test]
    public async Task RemoveAsync_NonExistentId_ReturnsFalse()
    {
        var wasRemoved = await _sut.RemoveAsync(Guid.NewGuid());
        Assert.That(wasRemoved, Is.False);
    }

    [Test]
    public async Task RemoveAsync_ExistingEntity_ReturnsTrue()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);

        var wasRemoved = await _sut.RemoveAsync(entity.Id);
        Assert.That(wasRemoved, Is.True);
    }

    [Test]
    public async Task RemoveAsync_ExistingEntity_IsNoLongerReadable()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);
        await _sut.RemoveAsync(entity.Id);

        var readBack = await _sut.ReadAsync(entity.Id);
        Assert.That(readBack, Is.Null);
    }

    // ── Cancellation ──────────────────────────────────────────────────────

    [Test]
    public void ReadAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var ct = new CancellationToken(canceled: true);
        Assert.ThrowsAsync<OperationCanceledException>(() => _sut.ReadAsync(ct));
    }

    [Test]
    public void SaveAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var ct = new CancellationToken(canceled: true);
        Assert.ThrowsAsync<OperationCanceledException>(() => _sut.SaveAsync(TestEntity.CreateSample(), ct));
    }

    [Test]
    public void RemoveAsync_CancelledToken_ThrowsOperationCanceled()
    {
        var ct = new CancellationToken(canceled: true);
        Assert.ThrowsAsync<OperationCanceledException>(() => _sut.RemoveAsync(Guid.NewGuid(), ct));
    }

    // ── Persistence ───────────────────────────────────────────────────────

    [Test]
    public async Task SaveAsync_WritesJsonFileToDisk()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);

        Assert.That(File.Exists(_testFilePath), Is.True);
    }

    [Test]
    public async Task Persistence_DataSurvivesNewInstance()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);
        _sut.Dispose();

        using var newInstance = new JsonFileDataAccess<TestEntity>(_settings);
        var readBack = await newInstance.ReadAsync(entity.Id);

        Assert.That(readBack, Is.Not.Null);
        entity.AssertEquivalentTo(readBack!);
    }

    [Test]
    public async Task Persistence_ComplexStructureSurvivesRoundTrip()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);
        _sut.Dispose();

        using var newInstance = new JsonFileDataAccess<TestEntity>(_settings);
        var readBack = await newInstance.ReadAsync(entity.Id);

        Assert.That(readBack, Is.Not.Null);
        entity.AssertEquivalentTo(readBack!);
    }

    [Test]
    public async Task Persistence_RemoveIsPersisted()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);
        await _sut.RemoveAsync(entity.Id);
        _sut.Dispose();

        using var newInstance = new JsonFileDataAccess<TestEntity>(_settings);
        var readBack = await newInstance.ReadAsync(entity.Id);

        Assert.That(readBack, Is.Null);
    }

    [Test]
    public async Task Persistence_MultipleEntitiesSurviveRoundTrip()
    {
        var e1 = TestEntity.CreateSample();
        var e2 = TestEntity.CreateSample();
        var e3 = TestEntity.CreateSample();
        await _sut.SaveAsync(e1);
        await _sut.SaveAsync(e2);
        await _sut.SaveAsync(e3);
        _sut.Dispose();

        using var newInstance = new JsonFileDataAccess<TestEntity>(_settings);
        var all = (await newInstance.ReadAsync()).ToList();

        Assert.That(all, Has.Count.EqualTo(3));
        Assert.That(all.Select(x => x.Id), Is.EquivalentTo(new[] { e1.Id, e2.Id, e3.Id }));
    }

    // ── OnlySaveOnDispose ─────────────────────────────────────────────────

    [Test]
    public async Task OnlySaveOnDispose_DoesNotWriteFileBeforeDispose()
    {
        var lazySetting = new JsonStorageSettings<TestEntity>
        {
            StorageFilePath = _testFilePath,
            OnlySaveOnDispose = true
        };
        using var lazySut = new JsonFileDataAccess<TestEntity>(lazySetting);

        await lazySut.SaveAsync(TestEntity.CreateSample());

        // File should NOT yet exist since we haven't disposed
        Assert.That(File.Exists(_testFilePath), Is.False);
    }

    [Test]
    public async Task OnlySaveOnDispose_WritesFileOnDispose()
    {
        var lazySetting = new JsonStorageSettings<TestEntity>
        {
            StorageFilePath = _testFilePath,
            OnlySaveOnDispose = true
        };
        var lazySut = new JsonFileDataAccess<TestEntity>(lazySetting);

        var entity = TestEntity.CreateSample();
        await lazySut.SaveAsync(entity);
        lazySut.Dispose();

        Assert.That(File.Exists(_testFilePath), Is.True);

        using var newInstance = new JsonFileDataAccess<TestEntity>(_settings);
        var readBack = await newInstance.ReadAsync(entity.Id);
        Assert.That(readBack, Is.Not.Null);
        entity.AssertEquivalentTo(readBack!);
    }

    // ── Observables ───────────────────────────────────────────────────────

    [Test]
    public async Task EntityAdded_FiresWhenNewEntityIsSaved()
    {
        TestEntity? received = null;
        _sut.EntityAdded.Subscribe(e => received = e);

        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);

        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Id, Is.EqualTo(entity.Id));
    }

    [Test]
    public async Task EntityUpdated_FiresWhenExistingEntityIsSaved()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);

        TestEntity? received = null;
        _sut.EntityUpdated.Subscribe(e => received = e);

        await _sut.SaveAsync(entity with { Name = "Changed" });

        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Name, Is.EqualTo("Changed"));
    }

    [Test]
    public async Task EntityAdded_DoesNotFireOnUpdate()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);

        var addCount = 0;
        _sut.EntityAdded.Subscribe(_ => addCount++);
        await _sut.SaveAsync(entity with { Name = "X" });

        Assert.That(addCount, Is.EqualTo(0));
    }

    [Test]
    public async Task EntityRemoved_FiresWithPreRemovalState()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);

        TestEntity? received = null;
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

    // ── Cache ─────────────────────────────────────────────────────────────

    [Test]
    public async Task Cache_SubsequentReadsDoNotHitDisk()
    {
        var entity = TestEntity.CreateSample();
        await _sut.SaveAsync(entity);

        // Modify the file on disk to simulate stale data — cache should still return the in-memory state
        File.WriteAllText(_testFilePath, "[]");

        var readBack = await _sut.ReadAsync(entity.Id);
        Assert.That(readBack, Is.Not.Null, "Cache should serve the entity, ignoring the modified file");
    }

    // ── Multiple entities ─────────────────────────────────────────────────

    [Test]
    public async Task ReadAsync_ReturnsAllSavedEntities()
    {
        var e1 = TestEntity.CreateSample();
        var e2 = TestEntity.CreateSample();
        var e3 = TestEntity.CreateSample();
        await _sut.SaveAsync(e1);
        await _sut.SaveAsync(e2);
        await _sut.SaveAsync(e3);

        var all = (await _sut.ReadAsync()).ToList();
        Assert.That(all, Has.Count.EqualTo(3));
    }
}

