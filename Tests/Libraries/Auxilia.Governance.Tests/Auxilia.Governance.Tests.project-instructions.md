# Auxilia.Governance.Tests

Unit and component tests for `Auxilia.Governance` — the Policy Engine, identity/RBAC, groups, and identity-import library. Unit tests (`UnitTests/` + root `GroupTests.cs`) wire services over in-memory storage; `ComponentTests/` builds a real `IHost` through `AddGovernance`.

## Special Rules
- `GovernanceTestContext` is the shared unit harness: every governance service over `InMemoryDataAccess`, seeded via helper methods — reuse it, don't hand-wire services per test.
- Secret-secrecy is an asserted invariant: import/protection tests verify plaintext secrets never appear in audit records or `ProtectedSettingsJson`. Keep new fixtures honouring it.

## File / Folder Map
```
Tests/Libraries/Auxilia.Governance.Tests/
├── GroupTests.cs                                # [Unit] GroupDirectory CRUD + PolicyEngine group-role grants
├── UnitTests/
│   ├── GovernanceTestContext.cs                 # shared harness: all services over InMemoryDataAccess
│   ├── PolicyEngineTests.cs                     # RBAC decisions, workflow-type access lists, allow/deny audit
│   ├── LocalIdentityProviderTests.cs            # password + API-key auth; PasswordHasher round-trip
│   ├── GroupMappingResolverTests.cs             # IdP group-claim → role resolution
│   ├── GovernanceSeederTests.cs                 # bootstrap-admin seeding + idempotence
│   └── IdentityImport/
│       ├── IdentityImportServiceTests.cs        # import semantics: create/update/disable, secret protection, audit
│       ├── CsvIdentityImportConnectorTests.cs   # CSV parse, quoting, bad-row reporting
│       ├── LdapIdentityImportConnectorTests.cs  # LDAP settings parse/defaults, group-DN extraction (protocol call untested here)
│       └── FakeIdentityImportConnector.cs       # scriptable IIdentityImportConnector test double
└── ComponentTests/
    └── GovernanceComponentTests.cs              # [Component] real IHost via AddGovernance: seed, auth, policy, audit trail
```
