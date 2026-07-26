# BackendService Retirement — Program Plan

> **Status:** ✅ COMPLETE · 2026-07-26 · Felix Klakow
> **Goal:** dissolve `Auxilia.BackendService`. Its backend/hosted services move into **Core.Api** / **WorkflowStudio**; its UI is rehomed as a proper **administrative console** (a pure `Auxilia.Core.Client` app — *not* called "BackendService"). Greenfield: the new path is the only path.

## 1. Target end-state (deployables after retirement)

| Deployable | Role | Gains in this program |
|---|---|---|
| **`Auxilia.Core.Api`** | control plane + kernel | **SSE live-view stream**, **failover/heartbeat monitor**, **audit-read** (+ MCP), **provider catalog**, **identity sources (+ stand-alone import)**. (Already owns identity/RBAC/groups/policy/audit, connectors, **schema registry**, Run API.) **Does NOT persist workflow configurations.** |
| **`Auxilia.Core.Runner`** | execution plane | unchanged (already owns dispatch, heartbeat-emit, view-data + status persistence) |
| **`Auxilia.WorkflowStudio`** | headless workflow product | owns the **persisted workflow configurations** (slot→connector refs, triggers), workflow **types/packages**, the **triggers** (scheduler + artifact-chaining) and **integration adapters** (email) — dispatching via the **Core Run API** (`ICoreClient`), not raw bus commands |
| **`Auxilia.AdminConsole`** *(new — ex-BackendService UI)* | operator/admin UI | Blazor Server, thin client (Core.Api for connectors/identity/audit/runs/SSE; the product for persisted configs); hosts all pages; **no** hosted/backend services |

### Model clarifications (2026-07-26, Felix)
- **Slots bind to connectors only** — the "slot instance" concept is **dropped**. A team mailbox = a **Mailbox connector granted to a group**. No slot-instance CRUD.
- **The Core does not persist/own workflow configurations** — the product owns the config store; the Core validates a submitted spec against its **schema registry** and runs it via the Run API. (The existing Core.Api `/api/configurations` is a leftover to move/deprecate — the product becomes the config owner.)
- **Provider catalog** and **identity sources** (with import for stand-alone / no-OIDC) are **Core admin** concerns → Core.Api.
- **Artifact-chaining is not a Core concept** — it survives as a **product trigger** (Studio); the visual flow-graph editor is product/console, optional.
| ~~`Auxilia.BackendService`~~ | — | **deleted** |

**Key architectural decisions (defaults — flag to change):**
- **D1 — One UI app, pure Core client.** The admin/operator UI becomes `Auxilia.AdminConsole`, talking *only* to Core.Api (runs, connectors, identity, slots, provider-catalog, audit, configs, SSE). Studio stays **headless**. (Rationale: Core owns configs + the schema registry + validation, so the config-editor UI is a pure Core client; avoids two Blazor apps.)
- **D2 — Triggers/adapters dispatch via the Core Run API**, not by publishing raw `RunWorkflowCommand` to the bus. The Core is the auth authority; the Product is a client.
- **D3 — Live push lives on Core.Api as SSE** (replacing the BackendService SignalR `/hubs/views` + in-process broker). Also unblocks the steering integration's stream.

## 2. Ownership moves (from the code investigation)

| BackendService component | → Target | Notes |
|---|---|---|
| `HeartbeatMonitor` (failover: re-dispatch orphaned runs) | Core.Api hosted service | reads runner `ServiceHeartbeatRecord`s; **no replacement today** — highest risk |
| `TriggerScheduler` (interval triggers) | WorkflowStudio | re-point to Core Run API (D2) |
| `ArtifactTriggerHandler` (workflow chaining, `workflow.artifact-events`) | WorkflowStudio | sole consumer of that exchange; re-point to Core Run API |
| `EmailTaskSourceAdapter` host (`Auxilia.Adapters.Email`) | WorkflowStudio | adapter code already separate — move the **host registration** + `MailboxTriggers` config |
| `WorkflowStatusEventHandler` + `ViewDataFanOutHandler` + `LiveViewBroker` + `ViewDataHub` (`/hubs/views`) | Core.Api SSE | persistence already covered (Core.Api `RunTrackingService`, Core.Runner `ViewDataHandler`); only **live push** must be rebuilt |
| `WorkflowConfigurationEditorService` | WorkflowStudio (owns the config store) | Core does not persist configs; AdminConsole edits via the product, dispatches via the Run API |
| `SlotInstanceService` | **dropped** | slots bind to connectors; a team mailbox = a Mailbox connector granted to a group |
| `ProviderCatalogService` + identity-source import | Core.Api | provider catalog + identity sources are Core admin (1d) |
| `read_audit` MCP | Core.Api ✅ (1c) | `get_view_data`/`list_views` deferred to the UI phase |
| Blazor pages + `ViewRenderer`/`AgentChatRenderer` + connect-flows | `Auxilia.AdminConsole` | rehomed UI, repointed to Core.Api + SSE |
| `QueueInitializer`, `IdentificationRequestHandler`, `ServiceInfo`, telemetry | delete | BackendService-local plumbing |

## 3. Phase sequence (each phase builds + tests green before the next)

- **Phase 1 — Core.Api backend capabilities (additive; nothing removed yet):**
  1a. **SSE live-view stream** (status + view-data) + `ICoreClient.StreamRunAsync`. *(also unblocks steering)*
  1b. **Failover/heartbeat monitor** hosted service.
  1c. **Audit-read** endpoint + MCP `read_audit`; **view-read** (`get_view_data`/`list_views`).
  1d. Admin endpoints Core lacks: **slot instances**, **provider catalog**, **identity-source import** (+ `ICoreClient` methods).
- **Phase 2 — WorkflowStudio product host:** move `TriggerScheduler` + `ArtifactTriggerHandler` + `EmailTaskSourceAdapter` into Studio, dispatching via the Core Run API (D2); move workflow-type/package registration.
- **Phase 3 — `Auxilia.AdminConsole`:** rename/rehome the BackendService UI; strip every hosted/backend service; make it a pure Core client on Core.Api + SSE; repoint all pages; re-home component tests.
- **Phase 4 — System tests + DevStand:** retarget the four env s (`SingleBackendService`, `DualBackend`, `EndToEnd`, `Failover`) + DevStand's demo to AdminConsole / Studio / Core.Api. Update `docs/TestStrategy.md` + the suite's project-instructions.
- **Phase 5 — Delete + finalize:** remove `Source/Auxilia.BackendService` + `Auxilia.BackendService.Tests`; re-home surviving unit tests; update `Auxilia.slnx`; verify no cross-service DB access; retire the legacy `ConnectorRecord` once unused.

## 4. Deletion checklist (Phase 5) ✅
- ✅ `Auxilia.slnx`: removed the two BackendService entries (folder `/04_BackendDomain/`).
- ✅ Deleted `Source/Auxilia.BackendService/` and `Auxilia.BackendService.Tests/` (incl. `Dockerfile`, launch profiles).
- ✅ Confirmed nothing in production references it (leaf host; only its own test project did — now gone).
- ✅ Migrated unit tests re-homed to their new owners (Core.Api / Studio / AdminConsole) in earlier phases.
- ⏳ Legacy `ConnectorRecord` cleanup remains a follow-up (verify no consumer before removal; `CoreConnectorRecord` is the live type).

## Progress

- **1a — Core.Api SSE live-view stream ✅** `GET /api/runs/{id}/stream` + `ICoreClient.StreamRunAsync`; `RunStreamBroker`/`RunStreamPublisher` over `workflow.status-events` + `workflow.view-data`; `RunStreamEvent` contract. 120/120 Core.Api tests.
- **1b — Core.Api failover monitor ✅** Bus-based, DB-isolation-respecting: new `RunnerHeartbeat` message (`platform.runner-heartbeats`) for liveness; `WorkflowStatusEvent` gained `OwnerServiceId`/`CommandId` for ownership; Core.Api persists the dispatch command in its own store and re-dispatches orphans. 132 Core.Api tests. *Transitional double-failover window vs BackendService's monitor closes in Phase 5.*
- **1c — audit-read ✅** `GET /api/audit` (typed filters + paging) + `read_audit` MCP + `ICoreClient.QueryAuditAsync`, reusing `audit.read`. 142/142 Core.Api tests.
- **1d — provider catalog ✅** migrated to Core.Api (`/api/provider-catalog` + 3 MCP tools + client), reusing `provider-catalog.manage`. Found it's an advisory *config-time* allowlist, never a dispatch gate — added no enforcement that didn't exist; locked with a test. 162 Core.Api tests. Note: the dispatch-enforced "which workflow types may run" gate is a separate `WorkflowCatalogRecord` → Studio.
- **1d — identity sources (+ stand-alone import) ✅** built the Core.Api REST + MCP + client surface over the existing Governance `IdentityImportService` (previously reachable only via BackendService's page); idempotent, secrets write-only, `identity-source.manage`-gated. 175 Core.Api tests.

**Phase 1 complete ✅** — all Core.Api backend capabilities in place, additive, nothing removed yet.

### Phase 2 decisions (from the design pass)
- **Run-as:** add `run.on-behalf-of` to `/api/runs` — a service caller may name a target principal; the Core evaluates `workflow.trigger` against the **target** and audits the delegation. (Service layer already threads `triggeredBy → TriggeredByPrincipalId`.)
- **Config store:** the ownership-move (Core → product) is **deferred to a distinct post-retirement phase** — it deletes a delivered API (`/api/configurations*`) and reworks the `CoreApiDispatch` acceptance test, and is tangled with a legacy/new two-store duality. For the retirement, triggers dispatch **stored configs via the Core Run API with on-behalf-of** (config stays in Core for now). Your "Core doesn't own configs" model still holds — it moves as its own step.
- **Studio ↔ bus:** Studio keeps a read-only subscription for `workflow.artifact-events`; dispatch goes via the Run API.
- **Mailbox:** adapter moves first (keeps its mailbox-cred read); convert the mailbox to a Core connector as a distinct follow-up.
- **E2E:** `EndToEndEnvironment` gains a Studio container for the mail path (partial Phase-4 pull-forward) to keep the suite green.

- **2a — `run.on-behalf-of` on `/api/runs` ✅** honors `RunRequest.RequestedBy` when the caller holds the permission (granted to Administrator + Operator); policy-checks the target for `workflow.trigger`; audits the delegation. 180 Core.Api tests.
- **2c-A — on-behalf-of on `/api/configurations/{id}/run` ✅** optional `onBehalfOf`; 185 Core.Api tests.
- **2c-B — schedulers → Studio ✅** added optional `context` to the config-run endpoint; TriggerScheduler + ArtifactTriggerHandler now in Studio dispatching via `RunConfigurationAsync(onBehalfOf, context)`; entities in Studio DB; artifact handler keeps its inbound bus subscription. 187 Core.Api + 18 Studio tests. Additive (BackendService copies remain).
- **2c-C — email adapter → Studio ✅** `ITaskSourceRunDispatcher` seam; default `BusRunDispatcher` keeps BackendService on the bus (one-line change), Studio uses `CoreClientRunDispatcher`. Email 13 + Studio 19 + BackendService 197 tests green.

**Phase 2 code moves complete ✅** — triggers + email adapter run in Studio dispatching via the Run API (additive; BackendService copies remain). **2d (EndToEnd retarget + test re-home) folds into Phase 4** — the additive approach keeps the suite green until BackendService is deleted.

### Phase 3a — Core.Api gap-fills (in progress)
- **3a-i — principal administration ✅** `/api/principals` (list/create human+AI-key, assign/revoke Direct roles, enable/disable) + 6 MCP tools + client; `principal.administer`-gated; one-time API-key handling preserved. 203 Core.Api tests.
- **Console auth = Option A (per-user bearer)** — on sign-in Core issues a short-lived signed per-user token; the console's `Core.Client` forwards it; Core.Api validates it. (Felix's call.)
- **3a-ii — per-user bearer auth ✅** DataProtection-signed `auxu_` tokens; `POST /auth/token` (cookie-gated); `UserBearer` scheme validating signature/expiry/still-Active; `Core.Client` per-caller handler. 217 Core.Api tests. Remaining 3a gap-fills (persisted view-read, rerun) folded in incrementally.
- **Deployment:** AdminConsole + Core.Api run **same-origin (behind one gateway)** so the Core session cookie is shared and the console mints the bearer via `/auth/token` — 3a-ii works as-is. (Separate origins would need a redirect code-handoff; deferred.)

### Phase 3b — build `Auxilia.AdminConsole` (in progress)
- **3b-i — scaffold + auth + first page ✅** Blazor Server project (pure Core client, in `.slnx`), same-origin bearer handoff (`ConsoleCallerTokenProvider` → `/auth/token`), verbatim renderers/layout, `Audit` repointed, home page. 6 console + 78 Core.Api unit tests. *Caveat:* pooled-HttpClient vs circuit-scope token delegation to harden (app-key fallback works meanwhile).
- **3b-ii — Admin/Identity/Provider-catalog/Runs pages ✅** repointed to `ICoreClient`; per-user token delegation hardened (per-scope client + prerender→circuit relay via `PersistentComponentState`). 21 console tests. *(Provider-catalog "which workflows may run" half = "managed in Workflow Studio" placeholder; built-in role names are a local list — no Core list-roles endpoint.)*
- **3b-iii — live-view pages ✅** RunDetail decodes Core SSE `RunStreamEvent` through `ViewRenderer`/`AgentChatRenderer` (live-view path proven end-to-end by test); Dashboard (live-now + recent + client-side counts); Connectors (CRUD + grants + catalog-driven create form). 27 console tests.
- **3a-iii — expose workflow types + schemas ✅** new `WorkflowSchemaPublished` fanout (Runner emits at registration) cached into Core.Api's own store; `GET /api/workflow-types` + `/{type}/schema` + MCP + client; `workflow-configuration.manage`-gated. DB-isolation-clean, additive. 227 Core.Api tests.
- **3b-iv — Workflows config-editor ✅** list/run configs; pick type → schema → bind slots→connectors → `CreateConfigurationAsync`; triggers link out. 33 console tests. *Surfaced follow-ups: no config-update endpoint (create-only), `PackageUri` not exposed per type.*

**Phase 3b complete ✅** — `Auxilia.AdminConsole` is a full pure-Core-client Blazor app (11 pages, live SSE views, per-user bearer auth), all additive; BackendService still intact.

### Phase 4 — retarget system tests + DevStand (in progress)
Decision (Felix): **retarget + delete now**; deferred gaps (config-update, trigger-CRUD API, OAuth connect-flows, dashboard stats/pins, session terminal, persisted view-read, rerun, `RunStatus` duration) become tracked follow-ups. Docker system tests validate in **CI** (not locally).
- **4 — retarget** *(in progress)* — delete BackendService-only envs (`SingleBackendService`, `DualBackend`); retarget `EndToEnd` (email path → Studio via Run API) + `Failover` (monitor → Core.Api) onto the new hosts; repoint DevStand. Keep `dotnet build` + unit/component green.
- **4 — retarget ✅** deleted `SingleBackendService`/`DualBackend`; Failover → Core.Api monitor + 2 runners; EndToEnd → Studio + Core.Api (seeds via the real Core surface); added Studio Dockerfile; DevStand repointed; docs updated. Build + unit/component green. 6 Docker assumptions flagged for CI.

### Phase 5 — delete BackendService ✅
- **5 — delete ✅** removed `Source/Auxilia.BackendService` + `Auxilia.BackendService.Tests` (`git rm`) and both `.slnx` entries; pre-delete reference scan confirmed a clean leaf (only comments + a generated `.trx` mention it elsewhere; `Auxilia.Adapters.Email.BusRunDispatcher` survives as an unused-but-valid impl). Build green (0 errors); all Unit/Component tests green across every project. Migrated-behavior coverage verified: Failover → `Auxilia.Core.Api.Tests` (`FailoverMonitorTests`/`FailoverMonitorComponentTests`/`RunnerLivenessTrackerTests`); triggers + email dispatch → `Auxilia.WorkflowStudio.Tests` (`TriggerSchedulerTests`/`ArtifactTriggerHandlerTests`/`EmailAdapterCoreDispatchTests`); connectors/identity/audit/provider-catalog/principals → `Auxilia.Core.Api.Tests`; view rendering → `Auxilia.AdminConsole.Tests`. ARCHITECTURE/CLAUDE finalized.

**Program complete.** The split platform (Core.Api + Core.Runner + WorkflowStudio + AdminConsole) is the only path. Tracked follow-ups (not blockers, deliberately deferred): config-update endpoint (configs are create-only), `PackageUri` exposed per workflow type, trigger-CRUD API, OAuth connect-flows, persisted view-read (SSE is live-only), rerun endpoint, dashboard stats/pins, `RunStatus` duration/lifetime field completeness, session terminal proxy; plus the Phase-4 items — the 6 Docker system-test assumptions validated in **CI** (not locally) and the DevStand screenshot harness (parked until AdminConsole capture is wired). `Auxilia.Adapters.Email.BusRunDispatcher` remains available but is no longer registered (its only registration was BackendService's `Program.cs`).

### Phase 3 design ✅ (AdminConsole rehome mapped)
- **Shape confirmed:** one Blazor Server app, pure `Auxilia.Core.Client`; live views over `ICoreClient.StreamRunAsync` (decode the `RunStreamEvent` envelope into the existing `ViewRenderer`/`AgentChatRenderer`, which move **verbatim**); auth = OIDC/cookie **relying-party on Core** + a new **per-caller bearer handler** in `Core.Client` (today it only carries a static app API key).
- **Pages:** REPOINT — `Audit`, `AdminIdentitySources`, `AdminProviderCatalog` (provider half), `Runs`. REWRITE — `Dashboard`, `RunDetail`, `Connectors`, `Workflows`/`WorkflowEditor`, `Admin`. DROP — `Login` (→ Core `/auth`), `MySlots`/`OperatorSlots` (slot-instances dropped), `WorkflowFlows`/`WorkflowFlow` (artifact-graph, not a Core concept).
- **Phase 3 splits: 3a Core.Api gap-fills → 3b build the console.** Gap-fills, priority order:
  1. **Principal administration** (list/create human+AI, assign/revoke role, disable) — no Core client today; **blocks `Admin`**.
  2. **Per-caller identity in `Core.Client`** (delegating handler forwarding the signed-in user's bearer).
  3. **Persisted view-read** (`get_view_data`/`list_views`) — SSE is live-only; needed to backfill run history.
  4. **Local password sign-in** on Core (stand-alone / no-OIDC) — Core is OIDC+API-key+cookie only today.
  5. Rerun endpoint; dashboard pins + stats; connect-flows (OAuth); `RunStatus` field completeness (views/dispatch-command/lifetime).

## 5. Risks
- **Failover gap:** nothing re-dispatches orphaned runs until 1b lands — do it before deleting BackendService.
- **Cross-service data:** Core.Api reading runner heartbeats / view-data crosses the Api/Runner DB split — resolve via the shared Core DB tier or a bus/API path (decide in 1b/1c).
- **System-test blast radius:** 4 environments + DevStand build the BackendService image; Phase 4 must land before Phase 5.
