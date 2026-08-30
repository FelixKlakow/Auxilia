# Auxilia.UniversalDataAccess

Backend-agnostic CRUD library. Exposes one interface (`IDataAccess<TEntity>`) with three interchangeable implementations: in-memory, JSON file, and MongoDB via EF Core. Emits reactive events on add/update/remove.

## Architecture

`SaveAsync` returns `true` if an existing entity was updated, `false` if it was newly added.
**Optimistic concurrency:** entities implementing `IVersionedEntity` carry a store-managed, monotonically increasing `long Version` (0 = never saved; every successful save stamps stored + 1 — callers never assign it). `TrySaveAsync(entity, expectedVersion)` is a compare-and-swap: it succeeds only if the stored version still equals `expectedVersion` (0 = must not exist yet) and never resurrects a removed entity; plain `SaveAsync` stays last-writer-wins but still advances the version atomically. `TrySaveAsync` on a non-versioned entity throws `NotSupportedException`. MongoDB enforces this via an EF concurrency token on `Version` (filtered replace), the in-memory/JSON stores via their write lock.
`JsonFileDataAccess` lazy-loads from disk on first access (inside a write lock). With `OnlySaveOnDispose = true` it batches all writes to `Dispose` time.
`MongoDbEfDataAccess` creates a fresh `DbContext` per operation via `IDbContextFactory` — it is never long-lived.
`ReaderWriterLockSlimExtensions` adds `.Read()` / `.Write()` returning `IDisposable` for `using`-block safety.

## File / Folder Map
```
Source/Libraries/Auxilia.UniversalDataAccess/
├── IDataAccess.cs                      # CRUD + TrySaveAsync (CAS) + EntityAdded/Updated/Removed observables
├── IEntity.cs                          # Guid Id { get; }
├── IVersionedEntity.cs                 # IEntity + store-managed long Version (optimistic concurrency)
├── DependencyInjectionExtensions.cs    # AddInMemoryStorage / AddJsonStorage / AddMongoDbStorage
├── ReaderWriterLockSlimExtensions.cs   # .Read() / .Write() → IDisposable
├── Implementations/
│   ├── InMemoryDataAccess.cs           # Dictionary<Guid,T>; thread-safe via lock
│   ├── JsonFileDataAccess.cs           # File-backed; lazy load; ReaderWriterLockSlim; IDisposable
│   ├── MongoDbEfDataAccess.cs          # EF Core + MongoDB; IDbContextFactory per operation
│   └── MongoDbContext.cs               # DbContext with single DbSet<TEntity>
└── Settings/
    ├── JsonStorageSettings.cs          # StorageFilePath, OnlySaveOnDispose
    └── MongoDbSettings.cs              # ConnectionString, DatabaseName, CollectionName
```