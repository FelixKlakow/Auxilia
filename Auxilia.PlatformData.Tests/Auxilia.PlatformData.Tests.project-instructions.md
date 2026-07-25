# Auxilia.PlatformData.Tests

Unit tests (`Category=Unit`) for `Auxilia.PlatformData` — the platform persistence layer: deterministic IDs, at-rest settings protection, DI entity wiring, and the filesystem artifact store. All in-process; the Json/artifact fixtures write to temp directories and clean up on teardown — no MongoDB/Testcontainers here (that lives in `Auxilia.UniversalDataAccess.Tests`).

## File / Folder Map
```
Auxilia.PlatformData.Tests/
└── UnitTests/
    ├── DeterministicGuidTests.cs              # v5 deterministic GUID: stability, part-boundary separation
    ├── AesGcmSettingsProtectorTests.cs        # enc1: AES-GCM protect/unprotect; random nonce; wrong-key + tamper rejection
    ├── DependencyInjectionExtensionsTests.cs  # AddPlatformEntity (InMemory + Json round-trip); AddSettingsProtection null vs AES
    ├── FileSystemArtifactStoreTests.cs        # artifact save/read, SHA-256 hash + size, per-work-item lineage versioning
    └── WorkflowConfigurationRecordTests.cs    # deterministic IdFor; no cross-entity-namespace collision
```
