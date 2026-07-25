# Auxilia.Workflows.Packer

.NET CLI tool (`PackAsTool`, command `auxilia-packer`) that builds and signs a workflow's
`*.workflow.zip` package: the workflow executable + an optional `data/` payload + an emitted
`workflow-schema.json` + a signed `package-manifest.json`.

## Special Rules

- The input directory must contain exactly one executable (`*.exe` on Windows, extensionless
  elsewhere); zero or multiple throws.
- The schema is produced by reflecting the workflow DLL in a collectible `AssemblyLoadContext`
  (whose `Load` resolves nothing; unloaded after use), finding `IWorkflowSchemaProvider` by
  interface FullName and invoking `GetSchema()` — there is no compile-time reference to the workflow.
- The RSA signature (SHA-256, PSS padding) covers the unsigned manifest, which carries a SHA-256
  hash of every packed file plus the schema — tampering with any entry breaks verification.
- Zip entry paths are always forward-slashed and relative to the input directory.

## File / Folder Map
```
Auxilia.Workflows.Packer/
├── Program.cs                # CLI entry: --input <dir> --output <file> --key <pem-path>
├── WorkflowPacker.cs         # Pack(): find exe, emit+hash schema, hash files, write signed manifest into the zip
└── RsaFileSigningProvider.cs # RSA-PSS/SHA-256 signer from a PEM private key; exposes SubjectPublicKeyInfo (base64)
```
