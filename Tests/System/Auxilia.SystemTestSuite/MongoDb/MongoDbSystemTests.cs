using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;
using Auxilia.UniversalDataAccess.Settings;
using Microsoft.EntityFrameworkCore;

namespace Auxilia.SystemTestSuite.MongoDb;

// ── Entity types ────────────────────────────────────────────────────────────

/// <summary>General-purpose entity used for CRUD system tests.</summary>
public sealed class MongoSystemEntity : IEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool Active { get; set; }
    public DateTime Timestamp { get; set; }
}

/// <summary>Version 1 of an entity — used to seed data for the migration scenario.</summary>
public sealed class MigrationEntityV1 : IEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = string.Empty;
    public int Rating { get; set; }
}

/// <summary>
/// Version 2 of the same entity, stored in the same MongoDB collection as V1.
/// <see cref="Bonus"/> is <c>int?</c> — when reading a V1 document that didn't have
/// this field, MongoDB EF Core will return <c>null</c> (absent field).
/// Application code uses <c>Bonus ?? 42</c> to obtain the intended default.
/// </summary>
public sealed class MigrationEntityV2 : IEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Label { get; set; } = string.Empty;
    public int Rating { get; set; }
    public int? Bonus { get; set; } // nullable — absent in old V1 documents
}

// ── Helpers ─────────────────────────────────────────────────────────────────

file static class DataAccessFactory
{
    /// <summary>Creates a ready-to-use <see cref="MongoDbEfDataAccess{TEntity}"/> from explicit settings.</summary>
    public static MongoDbEfDataAccess<TEntity> Create<TEntity>(MongoDbSettings<TEntity> settings)
        where TEntity : class, IEntity
    {
        var options = new DbContextOptionsBuilder<MongoDbContext<TEntity>>()
            .UseMongoDB(settings.ConnectionString, settings.DatabaseName)
            .Options;

        return new MongoDbEfDataAccess<TEntity>(new WireFactory<TEntity>(options, settings));
    }

    /// <summary>Convenience overload using default settings for <typeparamref name="TEntity"/>.</summary>
    public static MongoDbEfDataAccess<TEntity> Create<TEntity>(
        string connectionString,
        string databaseName,
        string? collectionName = null)
        where TEntity : class, IEntity
        => Create(new MongoDbSettings<TEntity>
        {
            ConnectionString = connectionString,
            DatabaseName = databaseName,
            CollectionName = collectionName ?? typeof(TEntity).Name
        });

    private sealed class WireFactory<TEntity>(
        DbContextOptions<MongoDbContext<TEntity>> options,
        MongoDbSettings<TEntity> settings)
        : IDbContextFactory<MongoDbContext<TEntity>>
        where TEntity : class, IEntity
    {
        public MongoDbContext<TEntity> CreateDbContext() => new(options, settings);

        public Task<MongoDbContext<TEntity>> CreateDbContextAsync(CancellationToken _ = default)
            => Task.FromResult(CreateDbContext());
    }
}

// ── Test fixture ─────────────────────────────────────────────────────────────

[TestFixture]
[Category("System")]
public class MongoDbSystemTests
{
    // Fresh DB name per test — NUnit reuses a single fixture instance, so we set
    // it in [SetUp] to prevent tests from sharing documents.
    private string _dbName = null!;

    [SetUp]
    public void SetUp() => _dbName = $"auxilia-test-{Guid.NewGuid():N}";

    private string ConnectionString => MongoDbEnvironment.ConnectionString;

    // ── CRUD ───────────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(30_000)]
    public async Task SaveAndRead_EntityIsPersistedAndCanBeRetrieved(CancellationToken ct)
    {
        using var access = DataAccessFactory.Create<MongoSystemEntity>(ConnectionString, _dbName);

        var entity = new MongoSystemEntity
        {
            Id = Guid.NewGuid(),
            Name = "Persist Me",
            Version = 1,
            Active = true,
            Timestamp = new DateTime(2026, 5, 4, 0, 0, 0, DateTimeKind.Utc)
        };

        await access.SaveAsync(entity, ct);

        var readBack = await access.ReadAsync(entity.Id, ct);

        Assert.That(readBack, Is.Not.Null);
        Assert.That(readBack!.Name, Is.EqualTo(entity.Name));
        Assert.That(readBack.Version, Is.EqualTo(entity.Version));
        Assert.That(readBack.Active, Is.EqualTo(entity.Active));
        Assert.That(readBack.Timestamp, Is.EqualTo(entity.Timestamp));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task SaveAndRead_DataPersistsAcrossNewInstance(CancellationToken ct)
    {
        var id = Guid.NewGuid();

        // First instance: write
        using (var write = DataAccessFactory.Create<MongoSystemEntity>(ConnectionString, _dbName))
        {
            await write.SaveAsync(new MongoSystemEntity { Id = id, Name = "Persistent", Version = 3 }, ct);
        }

        // Second instance: read
        using var read = DataAccessFactory.Create<MongoSystemEntity>(ConnectionString, _dbName);
        var readBack = await read.ReadAsync(id, ct);

        Assert.That(readBack, Is.Not.Null);
        Assert.That(readBack!.Name, Is.EqualTo("Persistent"));
        Assert.That(readBack.Version, Is.EqualTo(3));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Update_ChangesArePersisted(CancellationToken ct)
    {
        using var access = DataAccessFactory.Create<MongoSystemEntity>(ConnectionString, _dbName);

        var entity = new MongoSystemEntity { Id = Guid.NewGuid(), Name = "Original", Version = 1 };
        await access.SaveAsync(entity, ct);

        entity.Name = "Updated";
        entity.Version = 2;
        var wasUpdated = await access.SaveAsync(entity, ct);

        Assert.That(wasUpdated, Is.True);

        var readBack = await access.ReadAsync(entity.Id, ct);
        Assert.That(readBack!.Name, Is.EqualTo("Updated"));
        Assert.That(readBack.Version, Is.EqualTo(2));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Remove_EntityIsDeletedFromDatabase(CancellationToken ct)
    {
        using var access = DataAccessFactory.Create<MongoSystemEntity>(ConnectionString, _dbName);

        var entity = new MongoSystemEntity { Id = Guid.NewGuid(), Name = "ToDelete" };
        await access.SaveAsync(entity, ct);

        var removed = await access.RemoveAsync(entity.Id, ct);
        Assert.That(removed, Is.True);

        var readBack = await access.ReadAsync(entity.Id, ct);
        Assert.That(readBack, Is.Null);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Remove_IsPersisted_NewInstanceCannotFindDeletedEntity(CancellationToken ct)
    {
        var id = Guid.NewGuid();
        using (var write = DataAccessFactory.Create<MongoSystemEntity>(ConnectionString, _dbName))
        {
            await write.SaveAsync(new MongoSystemEntity { Id = id, Name = "X" }, ct);
            await write.RemoveAsync(id, ct);
        }

        using var read = DataAccessFactory.Create<MongoSystemEntity>(ConnectionString, _dbName);
        Assert.That(await read.ReadAsync(id, ct), Is.Null);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task ReadAsync_ReturnsAllStoredEntities(CancellationToken ct)
    {
        using var access = DataAccessFactory.Create<MongoSystemEntity>(ConnectionString, _dbName);

        for (var i = 0; i < 5; i++)
            await access.SaveAsync(new MongoSystemEntity { Id = Guid.NewGuid(), Name = $"Item {i}" }, ct);

        var all = (await access.ReadAsync(ct)).ToList();
        Assert.That(all, Has.Count.EqualTo(5));
    }

    // ── Observables ────────────────────────────────────────────────────────

    [Test]
    [CancelAfter(30_000)]
    public async Task EntityAdded_FiresWithCorrectEntity(CancellationToken ct)
    {
        using var access = DataAccessFactory.Create<MongoSystemEntity>(ConnectionString, _dbName);

        MongoSystemEntity? received = null;
        access.EntityAdded.Subscribe(e => received = e);

        var entity = new MongoSystemEntity { Id = Guid.NewGuid(), Name = "Observable" };
        await access.SaveAsync(entity, ct);

        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Id, Is.EqualTo(entity.Id));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task EntityRemoved_FiresWithRemovedEntity(CancellationToken ct)
    {
        using var access = DataAccessFactory.Create<MongoSystemEntity>(ConnectionString, _dbName);

        var entity = new MongoSystemEntity { Id = Guid.NewGuid(), Name = "ToRemove" };
        await access.SaveAsync(entity, ct);

        MongoSystemEntity? received = null;
        access.EntityRemoved.Subscribe(e => received = e);
        await access.RemoveAsync(entity.Id, ct);

        Assert.That(received, Is.Not.Null);
        Assert.That(received!.Id, Is.EqualTo(entity.Id));
    }

    // ── Schema migration (V1 → V2) ────────────────────────────────────────

    /// <summary>
    /// Builds settings for MigrationEntityV2 pointing at the shared V1 collection.
    /// No custom <c>ConfigureEntity</c> is needed: because <c>Bonus</c> is declared as
    /// <c>int?</c>, MongoDB EF Core automatically allows the field to be absent and
    /// returns <c>null</c> for missing fields — no exception, no custom converter.
    /// </summary>
    private MongoDbSettings<MigrationEntityV2> MigrationV2Settings(string collectionName) =>
        new()
        {
            ConnectionString = ConnectionString,
            DatabaseName = _dbName,
            CollectionName = collectionName
        };

    [Test]
    [CancelAfter(30_000)]
    public async Task Migration_EntityV2_ReadsV1Data_MissingFieldIsNullAndDefaultAppliesViaCoalescing(CancellationToken ct)
    {
        // The V1 and V2 types intentionally share the same collection name.
        const string sharedCollection = nameof(MigrationEntityV1);

        var id = Guid.NewGuid();

        // Step 1 — Write using V1 schema (no "Bonus" field in the document)
        using (var v1 = DataAccessFactory.Create<MigrationEntityV1>(ConnectionString, _dbName, sharedCollection))
        {
            await v1.SaveAsync(new MigrationEntityV1 { Id = id, Label = "Widget", Rating = 5 }, ct);
        }

        // Step 2 — Read back using V2 schema
        using var v2 = DataAccessFactory.Create(MigrationV2Settings(sharedCollection));

        var asV2 = await v2.ReadAsync(id, ct);

        Assert.That(asV2, Is.Not.Null, "V2 reader should find the document written by V1");
        Assert.That(asV2!.Label, Is.EqualTo("Widget"), "Existing fields must be preserved");
        Assert.That(asV2.Rating, Is.EqualTo(5), "Existing fields must be preserved");

        // A field absent in the V1 document is deserialized as null by MongoDB EF Core.
        // Application code obtains the intended default via null-coalescing: Bonus ?? 42.
        Assert.That(asV2.Bonus, Is.Null,
            "A field absent in the stored V1 document must be null — not an exception, not 0");
        Assert.That(asV2.Bonus ?? 42, Is.EqualTo(42),
            "Null-coalescing with the intended default (42) must yield 42");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Migration_AfterUpdatingV2Entity_NewValueIsRetained(CancellationToken ct)
    {
        const string sharedCollection = nameof(MigrationEntityV1);
        var id = Guid.NewGuid();

        // Write V1 data
        using (var v1 = DataAccessFactory.Create<MigrationEntityV1>(ConnectionString, _dbName, sharedCollection))
        {
            await v1.SaveAsync(new MigrationEntityV1 { Id = id, Label = "Widget", Rating = 5 }, ct);
        }

        // Read as V2 — confirm Bonus is null — then update it to 1337
        using (var v2Write = DataAccessFactory.Create(MigrationV2Settings(sharedCollection)))
        {
            var asV2 = await v2Write.ReadAsync(id, ct);
            Assert.That(asV2!.Bonus, Is.Null, "Pre-condition: field is absent in V1 data");

            asV2.Bonus = 1337;
            await v2Write.SaveAsync(asV2, ct);
        }

        // Verify the updated value survives a round-trip
        using var v2Read = DataAccessFactory.Create(MigrationV2Settings(sharedCollection));
        var updated = await v2Read.ReadAsync(id, ct);

        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.Bonus, Is.EqualTo(1337), "Updated Bonus must be persisted and read back correctly");
        Assert.That(updated.Label, Is.EqualTo("Widget"), "Other fields must remain intact");
        Assert.That(updated.Rating, Is.EqualTo(5), "Other fields must remain intact");
    }
}






