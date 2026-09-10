# Auxilia.PlatformData

Durable platform state: the entity records every platform service persists (workflow schemas, slot configurations, slot providers, signal handlers, run lifecycle records, audit log) plus backend selection and encryption-at-rest helpers. Built on `Auxilia.UniversalDataAccess` (`IDataAccess<TEntity>`); services own their repository logic, this project owns the shared shapes.

## Architecture

- Every entity gets a **deterministic ID** via `DeterministicGuid.For(...)` derived from its natural key (e.g. workflow type + slot name), so upserts from any replica converge on the same record. `For` length-prefixes every part before hashing, so part boundaries are part of the key — pass the natural-key components as separate parts and never add separator parts or join them yourself. Provider-type-keyed records (`SlotProviderRecord`, `ProviderCatalogRecord`, and the Core's `EnvironmentLayerRecord`) lowercase the provider type in `IdFor` — provider types are case-insensitive platform-wide, so Core store, dispatch lookup, and runner registry converge regardless of casing.
- `AddPlatformEntity<TEntity>` picks the storage backend per `PlatformDataSettings.Backend`: `InMemory` (tests), `Json` (dev/single node, default), `MongoDb` (production, shared across replicas).
- Secrets inside entities are stored **protected**: callers run settings through `ISettingsProtector` before persisting. `AesGcmSettingsProtector` is active when `ProtectionKeyBase64` is configured; otherwise `NullSettingsProtector` passes values through (dev only — log a warning at wiring time).
- `Artifacts/FileSystemArtifactStore` (the runner's payload backend, one singleton per process) assigns lineage versions under a per-lineage in-process gate and writes payloads to a `.pending` file that is moved into place only after the index row exists — a failed save leaves neither an orphaned payload nor a dangling index row.
- Entities are dumb records — no behaviour, no service dependencies, `Status`/`State` as strings so no service-layer enums leak in.

## File / Folder Map
```
Source/Libraries/Auxilia.PlatformData/
├── DeterministicGuid.cs                # UUIDv5-style: stable Guid from length-prefixed natural-key parts
├── PlatformDataSettings.cs             # Backend choice, JSON dir, Mongo conn, protection key
├── DependencyInjectionExtensions.cs    # AddPlatformEntity<TEntity> / AddSettingsProtection
├── Protection/
│   ├── ISettingsProtector.cs           # Protect / Unprotect strings
│   ├── AesGcmSettingsProtector.cs      # AES-GCM, "enc1:" + base64(nonce|cipher|tag)
│   └── NullSettingsProtector.cs        # Pass-through for dev
└── Entities/
    ├── WorkflowSchemaRecord.cs         # workflowType → schema JSON
    ├── SlotConfigurationRecord.cs      # workflowType+slot → provider, protected settings, status
    ├── SlotProviderRecord.cs           # providerType → slot-handler DLL path
    ├── SignalHandlerRecord.cs          # workflowType+signal → handler descriptor JSON
    ├── WorkflowInstanceRecord.cs       # run lifecycle: state, timestamps, error
    └── AuditRecord.cs                  # append-only audit entry
```
