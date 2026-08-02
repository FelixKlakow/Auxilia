# Auxilia.Workflows.Packer.Tests

Tests for `Auxilia.Workflows.Packer`, the workflow packaging/signing tool that produces signed `.workflow.zip` packages. Fixtures are uncategorized (no `[Category]`, so the pre-commit Unit filter skips them); each generates a real RSA key and packs to temp files.

## Special Rules
- Signing uses real `RSA` keys written to a temp PEM file — no mocks; assert against the actual PSS signature/public key.
- `WorkflowPackerTests` packs real directories to temp `.workflow.zip`s and reopens them; the no-provider case loads this test assembly as a stand-in DLL and forces GC before deleting it (Windows file-lock).

## File / Folder Map
```
Tests/Libraries/Auxilia.Workflows.Packer.Tests/
├── RsaFileSigningProviderTests.cs   # PEM-file RSA signer: PSS sign/verify, exported SPKI public key, wrong-key failure
└── WorkflowPackerTests.cs           # Pack → signed zip: expected entries, manifest file hashes/signature/public key, schema-emission failures
```
