# Auxilia.UniversalDataAccess.Tests

Unit tests for the three `IDataAccess<TEntity>` implementations. `MongoDbEfDataAccessTests` requires Docker (Testcontainers MongoDB container) and behaves as a component test despite living in the unit-test folder.

## File / Folder Map
```
Tests/Libraries/Auxilia.UniversalDataAccess.Tests/
└── UnitTests/
    ├── InMemoryDataAccessTests.cs      # Save add/update, Read, Delete, observable events
    ├── JsonFileDataAccessTests.cs      # File persist/load, OnlySaveOnDispose, events; writes to temp dir
    ├── MongoDbEfDataAccessTests.cs     # CRUD via real MongoDB container (Testcontainers)
    └── TestEntity.cs                   # Minimal IEntity: Guid Id, string? Name
```