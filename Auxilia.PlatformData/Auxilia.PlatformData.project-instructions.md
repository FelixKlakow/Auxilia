# Auxilia.PlatformData

Durable platform state: the entity records every platform service persists (workflow schemas, slot configurations, slot providers, signal handlers, run lifecycle records, audit log) plus backend selection and encryption-at-rest helpers. Built on `Auxilia.UniversalDataAccess` (`IDataAccess<TEntity>`); services own their repository logic, this project owns the shared shapes.

## Architecture

- Every entity gets a **deterministic ID** via `DeterministicGuid.For(...)` derived from its natural key (e.g. workflow type + slot name), so upserts from any replica converge on the same record.
- `AddPlatformEntity<TEntity>` picks the storage backend per `PlatformDataSettings.Backend`: `InMemory` (tests), `Json` (dev/single node, default), `MongoDb` (production, shared across replicas).
- Secrets inside entities are stored **protected**: callers run settings through `ISettingsProtector` before persisting. `AesGcmSettingsProtector` is active when `ProtectionKeyBase64` is configured; otherwise `NullSettingsProtector` passes values through (dev only — log a warning at wiring time).
- Entities are dumb records — no behaviour, no service dependencies, `Status`/`State` as strings so no service-layer enums leak in.

## File / Folder Map
```
Auxilia.PlatformData/
├── DeterministicGuid.cs                # UUIDv5-style: stable Guid from natural-key strings
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
