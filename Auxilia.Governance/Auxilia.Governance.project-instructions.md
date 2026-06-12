# Auxilia.Governance

Identity, accounts, and authorization for the platform (ARCHITECTURE.md §16, docs/governance-rbac-design.md). Every operation — human, AI, or service — is performed by an authenticated principal and authorized by the deny-by-default `IPolicyEngine`. Entity records live in `Auxilia.PlatformData`; this project owns the behavior.

## Architecture

- **Principals** (`PrincipalDirectory`): humans, AI agents, and services share one model. Local principals authenticate via `LocalIdentityProvider` (PBKDF2 passwords for humans, hashed API keys for AI/service principals); external IdPs are later `IIdentityProvider` drop-ins.
- **Roles are fixed in code** (`BuiltInRoles`: Administrator, Operator, User, Auditor — permission sets over `PermissionActions` constants). Assignments are stored per principal (`Direct` or `GroupMapping` source). Custom roles are a later, schema-compatible step.
- **`PolicyEngine.EvaluateAsync(PolicyContext)`** is the single authorization entry point: resolves the principal and its roles, checks workflow-type access lists first (when entries exist for the workflow type + action they are exclusive), falls back to role permissions, and **always appends an audit record** — allow and deny.
- **`GovernanceSeeder`** bootstraps the default tenant's initial administrator from configuration on first start; it never overwrites existing principals.
- Group claims from external IdPs translate to roles via `GroupMappingResolver` at session start.

## File / Folder Map
```
Auxilia.Governance/
├── PermissionActions.cs          # Action identifier constants (workflow.trigger, audit.read, ...)
├── BuiltInRoles.cs               # Role name → permission set; fixed in v1
├── Tenants.cs                    # DefaultTenantId for single-tenant v1
├── PrincipalDirectory.cs         # Principal + role assignment + credential administration
├── WorkflowTypeAccessStore.cs    # Per-workflow-type access list entries
├── GovernanceSeeder.cs           # First-start bootstrap administrator from config
├── GovernanceSettings.cs         # Bootstrap admin credentials section
├── DependencyInjectionExtensions.cs  # AddGovernance(...)
├── Policy/
│   ├── IPolicyEngine.cs / PolicyEngine.cs
│   ├── PolicyContext.cs          # (principal, action, resource, optional workflow type)
│   └── PolicyDecision.cs         # Allowed + Reason
└── Identity/
    ├── IIdentityProvider.cs      # AuthenticatePassword / AuthenticateApiKey → IdentitySession
    ├── IdentitySession.cs        # PrincipalId, Kind, resolved role names
    ├── LocalIdentityProvider.cs  # PBKDF2 passwords, SHA-256 API keys
    ├── PasswordHasher.cs         # pbkdf2:{iterations}:{salt}:{hash} envelope
    └── GroupMappingResolver.cs   # IdP group claims → role names
```

## Special rules
- Never log or audit credential material; audit records reference principals by ID.
- All policy decisions (allow AND deny) must go through `PolicyEngine` so the audit trail stays complete — no caller-side shortcuts.
