# Auxilia.UniversalDataAccess

Backend-agnostic CRUD library. Exposes one interface (`IDataAccess<TEntity>`) with three interchangeable implementations: in-memory, JSON file, and MongoDB via EF Core. Emits reactive events on add/update/remove.

## Architecture

`SaveAsync` returns `true` if an existing entity was updated, `false` if it was newly added.
`JsonFileDataAccess` lazy-loads from disk on first access (inside a write lock). With `OnlySaveOnDispose = true` it batches all writes to `Dispose` time.
`MongoDbEfDataAccess` creates a fresh `DbContext` per operation via `IDbContextFactory` — it is never long-lived.
`ReaderWriterLockSlimExtensions` adds `.Read()` / `.Write()` returning `IDisposable` for `using`-block safety.

## File / Folder Map
```
Source/Libraries/Auxilia.UniversalDataAccess/
├── IDataAccess.cs                      # CRUD + EntityAdded/Updated/Removed observables
├── IEntity.cs                          # Guid Id { get; }
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