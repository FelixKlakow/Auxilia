# Auxilia.Core.Api

The **Core control plane**: a standalone web service that authorizes, configures, and dispatches workflow *runs*, administers connectors and identity/groups, and exposes all of it over REST **and** authenticated MCP. It is the secure container runner's front door — it knows how to run workload containers correctly, not what a workflow *means* (that lives in `Auxilia.WorkflowStudio`).

Standalone deployable. Drives `Auxilia.Core.Runner` over the message bus; owns its own database, isolated from every other service.

## Load-bearing invariants (do not violate without asking)
- **Own isolated database (Principle 4).** `Program.cs` binds its own `PlatformData` section and registers only Core entities (`CoreRunConfigurationRecord`, `CoreConnectorRecord`, `CoreRunRecord`, `AuditRecord`). Never read or write another service's store; cross-service traffic is REST or the bus only.
- **Single authentication + audit authority.** The Core hosts governance (`AddGovernance`) and is the *only* place principals authenticate. Every REST endpoint and every MCP tool authenticates via API-key bearer (`CoreApiKeyAuthenticationHandler`), then runs a per-action `IPolicyEngine` check (`PermissionActions.*`) before doing anything — `MapMcp("/mcp").RequireAuthorization()` included. There is no unauthenticated or unauthorized path.
- **Secrets live here and only here — encrypted at rest, never returned.** `ConnectorService` protects settings with `ISettingsProtector` on write; reads (`ToDto`) return setting *keys*, never values; `ResolveSettingsAsync` decrypts only at dispatch time (JIT). Never log or audit secret values.
- **The Core is the authorization authority; the runner trusts it.** Run endpoints authorize the *Core-database* principal, then dispatch a self-contained inline `RunWorkflowCommand` with `RequestedBy = null`. The runner has a *separate* database and cannot resolve a Core principal, so it trusts Core-dispatched commands instead of re-authorizing. This is what lets the two services keep separate databases.
- **Runs are tracked by correlation, not shared state.** `RunTrackingService` (hosted) subscribes to `WorkflowStatusEvent` on the bus and updates `CoreRunRecord` (keyed by the runner's instance id) — the Core never reaches into the runner's store.
- **Minimal-API paging gotcha:** query endpoints put `int skip = 0, int take = 50` *last, with defaults*; a required-looking primitive query param returns HTTP 400 ("Required parameter was not provided").
- `public partial class Program;` at the bottom exists so `Auxilia.Core.Api.Tests` can host it via `WebApplicationFactory`.

## REST surface (every route `.RequireAuthorization()` + policy-checked)
- `POST /api/runs` (inline, "on the fly"), `GET /api/runs`, `GET /api/runs/{id}`, `POST /api/runs/{id}/cancel`
- `POST /api/configurations`, `GET /api/configurations`, `GET /api/configurations/{id}`, `POST /api/configurations/{id}/run`
- `POST /api/connectors`, `GET /api/connectors`, `GET /api/connectors/{id}`
- `POST /api/groups`, `GET /api/groups`, `POST /api/groups/{id}/members`, `POST /api/groups/{id}/roles` (all `principal.administer`)
- `/mcp` (authenticated MCP twin of the above), `GET /health`

## File / Folder Map
```
Source/Auxilia.Core.Api/
├── Program.cs                 # Host wiring; own DB; governance; API-key auth; REST + MCP; startup static-config seeding
├── CoreApiSettings.cs         # RunCommandQueue, CancelCommandQueue, StaticConfigurations (seed-on-boot)
├── GroupContracts.cs          # CreateGroupRequest / AddGroupMemberRequest / AssignGroupRoleRequest / GroupDto
├── Data/
│   ├── CoreRunConfigurationRecord.cs  # Stored runnable configuration (workflow type + slot bindings)
│   ├── CoreConnectorRecord.cs         # Connector; ProtectedSettingsJson (encrypted at rest)
│   └── CoreRunRecord.cs               # Run-tracking row, keyed by runner instance id
├── Services/
│   ├── ConnectorService.cs            # Protect on write; ResolveSettingsAsync (JIT); ToDto returns keys only
│   ├── RunConfigurationService.cs     # CRUD + EnsureAsync (idempotent static-config seeding)
│   ├── RunService.cs                  # Dispatch RunWorkflowCommand (RequestedBy=null); CancelAsync
│   ├── RunReadService.cs              # Filterable run queries over CoreRunRecord
│   └── RunTrackingService.cs          # Hosted; WorkflowStatusEvent -> CoreRunRecord
├── Auth/
│   ├── CoreApiKeyAuthenticationHandler.cs # Bearer API key -> IdentityProvider principal
│   ├── CoreClaims.cs                  # "auxilia:principal-id" claim; PrincipalIdOf(user)
│   ├── CoreSecuritySettings.cs        # BootstrapApiKey
│   ├── CoreSecurityBootstrap.cs       # Idempotently ensures the Administrator API-key principal
│   └── CoreAuthorization.cs           # Shared REST/MCP authorize helper (returns a 401/403 result, or null on allow)
└── Mcp/
    └── CoreMcpTools.cs                # Authenticated MCP tools (run / config / connector / group / query) — each policy-checked
```
