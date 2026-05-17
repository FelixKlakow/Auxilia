# Auxilia.Workflows.Tests

Unit tests for `Auxilia.Workflows`.

## File / Folder Map
```
Auxilia.Workflows.Tests/
├── WorkflowBuilderTests.cs            # Fluent API correctness, schema JSON output, duplicate-slot guard
├── WorkflowBuilderRunModeTests.cs     # Schema-export mode vs normal run; exit codes
├── WorkflowBootstrapperTests.cs       # Decrypt + ISlotHandler.Register called per slot
├── SlotHandlerRegistryTests.cs        # Register, resolve, missing-key throws
└── Crypto/
    ├── EphemeralKeyPairTests.cs        # Key generation, export, round-trip encrypt/decrypt
    └── SlotConfigurationCryptoTests.cs # Decrypt happy-path + corrupted ciphertext
```