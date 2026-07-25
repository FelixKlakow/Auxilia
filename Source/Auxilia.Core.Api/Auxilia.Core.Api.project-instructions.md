# Auxilia.Core.Api

The **Core control plane**: a standalone web service that authorizes, configures, and dispatches workflow *runs*, administers connectors and identity/groups, and exposes all of it over REST **and** authenticated MCP. It is the secure container runner's front door — it knows how to run workload containers correctly, not what a workflow *means* (that lives in `Auxilia.WorkflowStudio`).

Standalone deployable. Drives `Auxilia.Core.Runner` over the message bus; owns its own database, isolated from every other service.

## Load-bearing invariants (do not violate without asking)
- **Own isolated database (Principle 4).** `Program.cs` binds its own `PlatformData` section and registers only Core entities (`CoreRunConfigurationRecord`, `CoreConnectorRecord`, `CoreRunRecord`, `AuditRecord`). Never read or write another service's store; cross-service traffic is REST or the bus only.
- **Single authentication + audit authority.** The Core hosts governance (`AddGovernance`) and is the *only* place principals authenticate. Every REST endpoint and every MCP tool authenticates via **API-key bearer** (`CoreApiKeyAuthenticationHandler`) *or* an interactive **session cookie** (issued after Entra OIDC sign-in) — `AddCoreAuthentication` wires both schemes plus the optional `Oidc` scheme, and the default authorization policy accepts API-key **or** cookie. Both resolve to the same `auxilia:principal-id` claim, so the per-action `IPolicyEngine` check (`PermissionActions.*`) authorizes cookie and API-key callers identically — `MapMcp("/mcp").RequireAuthorization()` included. No credentials → a 401, never a browser redirect. There is no unauthenticated or unauthorized path (except `GET /health` and the token-authorized `resolve-slot`).
- **Interactive sign-in is JIT and secretless.** `GET /auth/login` challenges Entra (only when `Oidc:Enabled`); `GET /auth/callback` exchanges the validated external identity for a principal via `ExternalIdentityProvisioner` (keyed by `provider|subject`, **no credential record**, disabled accounts refused) and issues the session cookie. A freshly provisioned external user holds **no roles unless a group maps to one** — deny-by-default. **Directory groups drive roles (L2):** the provisioner reconciles `RoleAssignmentRecord`s tagged `Source = "GroupMapping"` from the token's group claims at every sign-in (grants new, revokes stale, never touches `Direct`/imported grants); the Policy Engine reads all sources by principal id, so directory roles need no Policy-Engine change. **Group-claim overage** (Entra drops the `groups` claim past ~200 memberships) is detected by `CoreClaims.HasGroupOverage` and read back from Microsoft Graph via `IDirectoryGroupResolver` (`GraphDirectoryGroupResolver` when OIDC is enabled, else a no-op) — a Graph failure degrades to no directory roles, never a blocked sign-in. Mappings are administered via `/api/identity/group-mappings` (see `docs/enterprise-login-design.md`).
- **Secrets live here and only here — encrypted at rest, never returned.** `ConnectorService` protects settings with `ISettingsProtector` on write; reads (`ToDto`) return setting *keys*, never values; `ResolveSettingsAsync` decrypts only at dispatch time (JIT). Never log or audit secret values.
- **Connectors are identity-linked and gated at dispatch (L3).** A connector has a `Scope` (`Company` = shared, `Personal` = owned by `OwnerPrincipalId`) and, for personal ones, grants admitting other principals / AD directory groups. `ConnectorAccessPolicy.CanUseAsync` decides eligibility; `RunService` enforces it at **dispatch** against the *triggering* principal for every connector a run's slot bindings reference — an ineligible trigger gets a 403 and nothing is staged. AD-group grants work because the provisioner persists `PrincipalRecord.DirectoryGroupsJson` at each sign-in. The `SlotCredentialResolver` does **not** re-check eligibility — it trusts the pre-authorized stash, same trust model as the rest of the runner boundary. Company connectors need `slot-config.write` to create; a personal connector is self-owned by any authenticated caller.
- **OBO delegation is session-lifetime — the only stored user secret is the access token (L4).** When `Oidc:EnableDelegation`, `/auth/callback` retains the user's access token encrypted (`DelegatedTokenStore` → `DelegatedUserTokenRecord`, principal-keyed, expiry, **no refresh token**). A slot binding with `DelegatedResource` set is resolved by exchanging that token on-behalf-of the run's *triggering* principal (recorded on `CoreRunResolutionRecord.TriggeredByPrincipalId`) via `IDelegatedTokenExchange` — the downstream token is minted **JIT and never persisted**, delivered as `{ accessToken }`. Delegated slots **fail closed** (no retained/expired token, or unattended run → the slot is rejected, never a silent fallback). This is the one place a user-derived secret is stored; keep it that way — no refresh tokens, no widening the retention.
- **The Core is the authorization authority; the runner trusts it.** Run endpoints authorize the *Core-database* principal, then dispatch a self-contained inline `RunWorkflowCommand` with `RequestedBy = null`. The runner has a *separate* database and cannot resolve a Core principal, so it trusts Core-dispatched commands instead of re-authorizing. This is what lets the two services keep separate databases.
- **Runs are tracked by correlation, not shared state.** `RunTrackingService` (hosted) subscribes to `WorkflowStatusEvent` on the bus and updates `CoreRunRecord` (keyed by the runner's instance id) — the Core never reaches into the runner's store.
- **Minimal-API paging gotcha:** query endpoints put `int skip = 0, int take = 50` *last, with defaults*; a required-looking primitive query param returns HTTP 400 ("Required parameter was not provided").
- `public partial class Program;` at the bottom exists so `Auxilia.Core.Api.Tests` can host it via `WebApplicationFactory`.

## REST surface (every route `.RequireAuthorization()` + policy-checked)
- `POST /api/runs` (inline, "on the fly"), `GET /api/runs`, `GET /api/runs/{id}`, `POST /api/runs/{id}/cancel`
- `POST /api/configurations`, `GET /api/configurations`, `GET /api/configurations/{id}`, `POST /api/configurations/{id}/run`
- `POST /api/connectors` (personal = self-owned; company needs `slot-config.write`), `GET /api/connectors`, `GET /api/connectors/{id}`, `POST /api/connectors/{id}/grants` (owner or `slot-config.write`)
- `POST /api/groups`, `GET /api/groups`, `POST /api/groups/{id}/members`, `POST /api/groups/{id}/roles` (all `principal.administer`)
- `GET/POST /api/identity/group-mappings`, `DELETE /api/identity/group-mappings/{id}` (directory group→role mappings; all `identity-source.manage`)
- `GET /auth/login`, `GET /auth/callback` (anonymous — the OIDC sign-in flow), `POST /auth/logout`, `GET /auth/me` (authenticated)
- `/mcp` (authenticated MCP twin of the above), `GET /health`

## File / Folder Map
```
Source/Auxilia.Core.Api/
├── Program.cs                 # Host wiring; own DB; governance; API-key auth; REST + MCP; startup static-config seeding
├── CoreApiSettings.cs         # RunCommandQueue, CancelCommandQueue, StaticConfigurations (seed-on-boot)
├── GroupContracts.cs          # CreateGroupRequest / AddGroupMemberRequest / AssignGroupRoleRequest / GroupDto
├── GroupMappingContracts.cs   # CreateGroupMappingRequest / GroupMappingDto (directory group→role admin)
├── Data/
│   ├── CoreRunConfigurationRecord.cs  # Stored runnable configuration (workflow type + slot bindings)
│   ├── CoreConnectorRecord.cs         # Connector; ProtectedSettingsJson (encrypted); Scope/Owner/Grants (L3)
│   ├── CoreRunRecord.cs               # Run-tracking row, keyed by runner instance id
│   └── DelegatedUserTokenRecord.cs    # Retained user access token (encrypted, principal-keyed, expiry) for OBO (L4)
├── Services/
│   ├── ConnectorService.cs            # Protect on write; ResolveSettingsAsync (JIT); ToDto returns keys only; owner/scope/grants
│   ├── ConnectorAccessPolicy.cs       # CanUseAsync gate (company/owner/principal-grant/AD-group-grant) + ConnectorAccessDeniedException
│   ├── DelegatedTokenStore.cs         # Retain/get the user's access token (encrypted, session-lifetime) for OBO
│   ├── IDelegatedTokenExchange.cs     # OBO exchange seam + NullDelegatedTokenExchange (fails closed)
│   ├── EntraOboTokenExchange.cs       # Real on-behalf-of grant to the Entra token endpoint (wired when EnableDelegation)
│   ├── RunConfigurationService.cs     # CRUD + EnsureAsync (idempotent static-config seeding)
│   ├── RunService.cs                  # Dispatch RunWorkflowCommand (RequestedBy=null); gates connectors vs. triggering principal; CancelAsync
│   ├── RunReadService.cs              # Filterable run queries over CoreRunRecord
│   └── RunTrackingService.cs          # Hosted; WorkflowStatusEvent -> CoreRunRecord
├── Auth/
│   ├── CoreApiKeyAuthenticationHandler.cs # Bearer API key -> IdentityProvider principal
│   ├── CoreAuthExtensions.cs          # AddCoreAuthentication: API-key + Cookie + optional Entra OIDC; multi-scheme default policy; wires IDirectoryGroupResolver
│   ├── AuthSchemes.cs                 # Scheme name constants (ApiKey / Cookie / Oidc / ExternalCookie)
│   ├── OidcSettings.cs                # Entra relying-party config (bound from Oidc:*); Enabled gates the OIDC scheme
│   ├── CoreClaims.cs                  # "auxilia:principal-id" claim; PrincipalIdOf; ExternalIdentityFromPrincipal; HasGroupOverage
│   ├── IDirectoryGroupResolver.cs     # Overage seam + NullDirectoryGroupResolver (no-op default)
│   ├── GraphDirectoryGroupResolver.cs # Reads >~200-group memberships from Microsoft Graph (delegated); wired when OIDC enabled
│   ├── CoreSecuritySettings.cs        # BootstrapApiKey
│   ├── CoreSecurityBootstrap.cs       # Idempotently ensures the Administrator API-key principal
│   └── CoreAuthorization.cs           # Shared REST/MCP authorize helper (returns a 401/403 result, or null on allow)
└── Mcp/
    └── CoreMcpTools.cs                # Authenticated MCP tools (run / config / connector (+grants) / group / group-mapping / query) — each policy-checked
```
