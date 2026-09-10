# Auxilia.UniversalDataAccess.Tests

Unit tests for the three `IDataAccess<TEntity>` implementations. `MongoDbEfDataAccessTests` exercises `MongoDbEfDataAccess` through the EF Core InMemory provider (mocked context factory) — no Docker required.

## File / Folder Map
```
Tests/Libraries/Auxilia.UniversalDataAccess.Tests/
└── UnitTests/
    ├── InMemoryDataAccessTests.cs      # Save add/update, Read, Delete, observable events
    ├── JsonFileDataAccessTests.cs      # File persist/load, OnlySaveOnDispose, events; writes to temp dir
    ├── MongoDbEfDataAccessTests.cs     # CRUD via the EF InMemory provider (mocked IDbContextFactory)
    ├── ConditionalSaveTests.cs         # TrySaveAsync (CAS) contract per backend + version bumping
    ├── MongoDbEfDataAccessWriteRaceTests.cs # versioned save/remove races via a SaveChanges interceptor: bounded retries, real errors surface
    └── TestEntity.cs                   # Sample IEntity (scalars, collections, nested object)
```
