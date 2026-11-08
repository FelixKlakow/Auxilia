# Auxilia - Architecture Overview

> **Status:** v1.1 — Current (Core.Api / Core.Runner / AdminConsole + the workflow-domain client library)
> **Stack:** C# / ASP.NET Core / Blazor Server

---

> **This document describes the split architecture** delivered by the Core platform separation (**`docs/delivered/core-platform-separation-plan.md`**) and the Studio→library program (2026-08-01). The platform is:
> - **`Auxilia.Core.Api`** — the secure **control plane**: REST + OpenAPI, authenticated MCP, the identity / RBAC / groups / policy / audit authority, connector administration (secrets encrypted at rest), the Run API, and the live-view + artifact SSE streams.
> - **`Auxilia.Core.Runner`** — the **execution plane** (formerly the *Steering Instance*): container launch, the authenticated JIT-credential handshake, workspace / network / resource mediation, and run lifecycle.
> - **`Auxilia.AdminConsole`** — the operator/admin Blazor UI, a pure `Auxilia.Core.Client` consumer with no database.
> - **`Auxilia.Workflows.Client`** — the workflow-domain **library** (successor of the retired `Auxilia.WorkflowStudio` deployable): schema-validated authoring against the Core registry, interval triggers, and artifact chaining over the Core's server-side-filtered artifact SSE stream. Hostable in any app; **`Auxilia.TriggerHost`** is the bundled always-on reference host and additionally carries the email work-item intake adapter.
>
> Each service owns its **own database**; secrets live only in the Core; audit is centralised in the Core. The original `Auxilia.BackendService` monolith has been **fully retired** (see `docs/backend-service-retirement-plan.md`), and `Auxilia.WorkflowStudio` followed on 2026-08-01 — the split platform plus the client-library family is now the only path.

---

## Table of Contents

1. System Overview
2. Core Concepts
3. High-Level Architecture
4. Key Components
5. AI Integration Layer
6. Workflow Lifecycle
7. Security and Signing
8. Resource Access Model
9. Workspace Management
10. Network Isolation Model
11. Multi-Account Model
12. Deployment Modes
13. Open Questions and Concerns
14. Scaling
15. Live View Data and Pluggable Dashboards
16. Governance and RBAC

---

## 1. System Overview

Auxilia is a **workflow-driven distributed system** where:

- **Work items** (from Jira, Azure DevOps, Trello, etc.) are the primary trigger for workflows.
- **Workflows** are stateful, signed programs that run to completion and communicate exclusively via the platform message bus.
- **AI agents** are first-class citizens - they can trigger, steer, observe, and complete workflows via the same interfaces as human users (MCP protocol).
- **Security is pluggable** - runs standalone out of the box, or integrates with corporate identity providers (AAD, LDAP, OIDC).
- **The platform is three deployables plus a client-library family** - a secure **Core** (`Core.Api` control plane + `Core.Runner` execution plane) that owns identity, secrets, and container execution, and the **Admin Console** operator/admin UI (a pure client of the Core Web API, no database). The **workflow domain is a library** (`Auxilia.Workflows.Client`: authoring, triggers, artifact chaining — all over the Core client surface, never the bus), embeddable in any host; `Auxilia.TriggerHost` is the bundled always-on reference host. Each service has its own database; they meet only over the Core API and the message bus.

---

## 2. Core Concepts

| Concept | Description |
|---|---|
| **Work Item** | A unit of work from an external task source (Jira issue, ADO ticket, Trello card, etc.) |
| **Workflow** | A signed, stateful, isolated program that processes a work item through defined steps until completion or cancellation |
| **Core.Api** | The secure control plane — REST + OpenAPI, authenticated MCP, the identity / RBAC / groups / policy / audit authority, connector administration, and the Run API |
| **Core.Runner** | The execution plane (formerly the *Steering Instance*) — launches and configures workload containers, performs the just-in-time credential handshake, and owns run lifecycle, ownership, and failover heartbeats |
| **Workflows.Client library** | The workflow-domain library — schema-validated authoring (against the Core registry), interval triggers, and artifact chaining over the Core's filtered artifact SSE stream; embeddable in any host app. `Auxilia.TriggerHost` is the bundled always-on reference host (+ email intake) |
| **Connector** | A stored, named set of credentials/settings for an external system, administered in the Core and **encrypted at rest**. Workflow configurations reference connectors by ID and never carry secret material (this is the concrete mechanism behind the *Account Bundle* concept used below) |
| **MCP Interface** | Model Context Protocol endpoint - gives AI agents full parity with the human UI; on the Core it is authenticated against the same policy authority as REST |
| **Account Bundle** | A set of credentials grouped by purpose (e.g. user identity, AI service, source control) |
| **Resource Proxy** | A platform-managed, audited access point through which workflows reach external APIs and databases |
| **Trust Chain** | Signature chain ensuring a workflow is authorized to run in this environment |
| **Artifact Input** | A named artifact from a prior workflow run injected into a new WorkflowContext as input |
| **Artifact Store** | Pluggable persistent store (`IArtifactStore`) where named artifacts survive between runs — payloads plus a metadata index for lineage, versioning, and dispatch-time resolution |
| **Workspace Manager** | Platform component that manages persistent per-repo warm caches and per-run isolated CoW snapshots |
| **Network Policy** | Per-workflow declarative allowlist of permitted outbound endpoints, enforced at the kernel network namespace level |
| **Package Proxy** | Optional platform-managed mirror of public package registries (NuGet, npm, PyPI, etc.) providing caching and security scanning |
| **Work Item Index** | Optional background-indexed store of historical work items enabling similarity search (deferred) |

---

## 3. High-Level Architecture

```mermaid
graph TB
    subgraph Sources["External Sources"]
        TASKS["Task Sources (Jira, ADO, TFS, Trello, GitHub)"]
        IDP["Identity Providers (AAD, LDAP, OIDC, Local)"]
        EXT["External Resources (APIs, DBs, CI, package registries)"]
    end
    subgraph Clients["Clients"]
        ADMIN["Admin Console (Auxilia.AdminConsole)*\nBlazor Server · pure Core.Client"]
        AI["AI Agents (MCP)"]
        CLI["CLI / external automation"]
    end
    subgraph Hosts["WORKFLOW-DOMAIN HOSTS (Auxilia.Workflows.Client · e.g. Auxilia.TriggerHost)"]
        PROD["Authoring helpers (schemas from the Core registry)\nInterval triggers · artifact chaining (Core SSE)\nEmail work-item intake (TriggerHost)\nDispatch via the Core Run API"]
    end
    subgraph Core["CORE (security kernel)"]
        API["Core.Api — control plane\nREST + OpenAPI · authenticated MCP\nIdentity / RBAC / groups / policy / audit\nConnectors (secrets, encrypted)\nRun API + filtered queries\nLive-view SSE · failover monitor\naudit-read · provider catalog · identity import"]
        RUNNER["Core.Runner — execution plane\nDocker launch · workspace / CoW\nnetwork policy · Resource Proxy\nJIT credential handshake · lifecycle\nownership · heartbeat · drain"]
    end
    WF["Workload Containers\n(own network + mount namespace)"]
    BUS["Message Bus (IMessageBusClient — RabbitMQ default)"]
    COREDB[("Core.Api DB")]
    RUNDB[("Core.Runner DB")]
    PRODDB[("Host-owned storage")]

    ADMIN -->|Core.Client REST| API
    API -.->|SSE live views| ADMIN
    CLI -->|REST| API
    AI -->|authenticated MCP| API
    PROD -->|Core.Client REST: connectors, schemas, runs| API
    PROD -->|start run — Run API| API
    API -.->|filtered artifact SSE| PROD
    TASKS -->|work-item events| PROD
    IDP -->|authenticate| API

    API -->|run request| BUS
    BUS -->|competing consume| RUNNER
    RUNNER -->|status + view-data events| BUS
    BUS -->|status + view-data fan-out| API

    RUNNER -->|launch + JIT encrypted slot creds| WF
    WF -->|resource requests via bus| RUNNER
    RUNNER -->|scoped, audited, default-deny egress| EXT

    API --> COREDB
    RUNNER --> RUNDB
    PROD --> PRODDB
```

<sub>\* The original `BackendService` monolith has been **retired** (`docs/backend-service-retirement-plan.md`). Its backend capabilities moved into Core.Api (live-view SSE, failover/heartbeat monitor, audit-read, provider catalog, identity import); its UI is rehomed as `Auxilia.AdminConsole` (a pure `Auxilia.Core.Client` app). The interim `Auxilia.WorkflowStudio` deployable retired in turn on 2026-08-01: its triggers (scheduler + artifact-chaining, now over the Core's filtered artifact SSE) live in the `Auxilia.Workflows.Client` library, its email intake in `Auxilia.TriggerHost`, and its private type catalog died in favour of the Core workflow-type registry. Core.Api and Core.Runner currently keep **separate** databases; consolidating them into one shared Core DB tier is planned — see §12 and the migration plan.</sub>

---

## 4. Key Components

### Core.Api — Control Plane
- The single **authentication + authorization authority**: hosts Governance (identity, principals, RBAC, **first-class groups**, the Policy Engine) and the audit authority. Every REST call and every MCP tool authenticates (API-key bearer) and is policy-checked per action — there is no unauthenticated or unauthorized path.
- **Connector administration**: stores connectors (credentials/settings for external systems) **encrypted at rest**; reads return setting *keys*, never values; secrets are decrypted only just-in-time at dispatch. Secrets live here and nowhere else.
- **Run API**: start a run inline ("on the fly"), from a stored configuration, or from a startup static seed; query runs with typed filters; cancel a run (a run still `Dispatched` — no runner has claimed it — is cancelled by the Core itself: versioned compare-and-swap to `Cancelled`, `Cancelled` status event; the resolution stash is kept for rerun because the resolver refuses every resolution for a terminal run; a claimed run's cancel is forwarded over the shared cancel queue, where whichever runner dequeues it forwards it unconditionally to the instance's `workflow-cancel-{id}` queue — pool-safe, the consumer need not own the instance). It authorizes the caller, then dispatches a **self-contained `RunWorkflowCommand`** to Core.Runner over the bus (the runner has a different database and cannot resolve a Core principal, so it trusts Core-dispatched commands rather than re-authorizing). The command also carries the registry's inspected **schema** as a cold-start seed: a runner that has never run the type makes its schema-driven pre-launch decisions (interactive terminal, network baseline, declared repositories) correctly on the very first dispatch; the runner's own store — refreshed by each run's registration — wins once populated.
- **Authenticated MCP** twin of the REST surface (`/mcp`), subject to the same policy authority — AI/service parity without a hand-maintained mirror.
- Stateless → scales horizontally behind a load balancer. Owns its own database.
- **Delivered** (BackendService retirement — `docs/backend-service-retirement-plan.md`): the **failover/heartbeat monitor** (bus-based liveness via `RunnerHeartbeat` + run-ownership carried on `WorkflowStatusEvent`), the **live-view SSE stream** (`GET /api/runs/{id}/stream`), **audit read** (`/api/audit`), the **provider catalog**, **identity-source import**, and **`run.on-behalf-of`** delegation on the Run API. Operator/admin UI is rehomed in `Auxilia.AdminConsole`.

### Core.Runner — Execution Plane
- The renamed, dissolved *Steering Instance*. Consumes run requests from Core.Api (competing consumers), **launches and configures the workload container correctly** — credentials, the run's own broker identity, its egress gateway, mounts, capability clamps (`--cap-drop ALL` + a minimal retained set, `no-new-privileges`), pids/memory/CPU ceilings, an optional OCI runtime class — and owns the full run lifecycle (see section 6). This is the single audited "configure the container correctly" chokepoint; §7 *Workload containment* describes what a container can and cannot reach.
- Performs the **authenticated registration handshake**: injects a one-time instance token at launch, pre-creates the instance's exclusive response queue, and delivers each slot's configuration **just-in-time per slot activation**, encrypted for that instance — never an upfront bundle, never before the slot is used. OAuth-backed connectors are **refreshed on delivery**: catalog entries may declare a data-driven `ProviderOAuthRefresh` spec (token endpoint, client id, which settings hold access/refresh/expiry) and the Core exchanges the refresh token whenever a stale credential is resolved — for slot delivery and connector browsing alike.
- Hosts the execution-side platform capabilities: the **Workspace Manager** (warm cache + per-run CoW snapshots), **environment-image composition** (capability layers onto the workflow image, content-addressed cache — §9), the **Network Egress Layer**, the **Resource Proxy**, artifact persistence, live view-data fan-out, and the drain coordinator for long-living workflows.
- Runs a **pre-flight plugin/image compatibility check** on baked-image launches: every member an injected slot plugin references on a shared assembly must exist in the image's copy — build skew fails the run as `PreFlightFailed` with a rebuild hint instead of a mid-session `MissingMethodException`; any pre-start launch failure is failed visibly, never stranded.
- Holds the **workflow schema registry** (`WorkflowSchemaStore`): at registration it stores each workflow type's embedded schema. The Core keeps the schema because it **validates every client-submitted configuration against it** — the client cannot self-certify — and so clients can fetch a schema to build a valid configuration.
- Emits ownership **heartbeats** for failover detection and publishes **status events** back to Core.Api (and to the dashboard).
- **Re-adopts its workflow containers at startup** (labels `auxilia.workflow=1` + `auxilia.instance-id`, label-scoped — nothing else on the Docker host is touched): a restarted runner RE-CLAIMS each matched non-terminal run under its fresh ServiceId (racing the Core's ~30s failover clock), re-attaches exit watchers to still-running containers, restores their persisted (protected) instance tokens so the in-container SDK keeps authenticating, collects the REAL exit code + log tail of containers that died during the downtime, fails records whose container vanished, and clean-kills only containers without a live run (removing their workspace/output roots). If the runner was down longer than the heartbeat window, the Core has already failed the run over — the late re-claim is dropped by the run-tracking **terminal sink** (a terminal record is never resurrected) and the queued per-run cancel stops the container. The FailoverMonitor's zombie sweep still covers runs whose owner was never heard from, and a failover **re-dispatch goes through the same machinery as an operator rerun** (fresh command id + resolution token, bindings re-stashed).
- **A crashed run leaves nothing behind**: when a container dies without reporting a terminal state, the exit watcher fails the run visibly (exit code + log tail), consumes the instance token, and sweeps its pod, repository workspace, and per-run output directory (nothing else will — a crashed workflow never sends the terminal state message that drives the graceful cleanup). A crash while `Draining` additionally dispatches the drain replacement (section 6).
- **No terminal path leaks run roots**: the same sweep (`RunRootsCleanup`) runs on the graceful terminal state (after declared outputs are persisted — Success, Failed and Cancelled alike), on a pre-flight failure (a check failing after the workspace was prepared would otherwise leave a clone on disk), and on re-adoption. The extracted ZIP package (`auxilia-wf-{instanceId}`, bind-mounted read-only) is deleted by the exit watcher on every container exit — never while the container may still be running.
- Scales via competing consumers on the run queue. Owns its own database.
- **Transition state:** Core.Runner still resolves credentialed slots from its own slot-configuration stores (seeded via the `slot-configurations` exchange); moving that resolution onto **Core connectors** and folding the runner's lifecycle store into a shared Core DB tier are the remaining consolidation steps.

### Workflows.Client — the workflow-domain library (+ Trigger Host)
- `Auxilia.Workflows.Client` owns the **workflow domain as a library**, embeddable in a server service or a desktop app: **schema-validated authoring** (`WorkflowAuthoring` — validates slot bindings against the Core registry schema, resolves connectors, configure + run in one call), the **interval scheduler**, and **artifact-chaining triggers**. Every dispatch goes through the **Core Run API** (`RunConfigurationAsync`/`RunAsync`, with `run.on-behalf-of` for the configured principal). There is **no library-side type catalog** — the Core workflow-type registry is the only catalog.
- A **pure client of the Core API** (`Auxilia.Core.Client`) with **zero bus access**: artifact chaining consumes the Core's **server-side-filtered artifact SSE stream** (`/api/artifacts/stream`, one consumer per distinct artifact type, reconnect with backoff). Trigger definitions are **host-owned** (`ITriggerStore`; in-memory default) — what-follows-what is workflow-domain policy, not Core state. Triggers fire only while some host embedding the library runs.
- **`Auxilia.TriggerHost`** is the bundled always-on reference host: the library engines as hosted services plus the **email work-item intake adapter** (`Auxilia.Adapters.Email`). Intake credentials (e.g. the mailbox password) are **host configuration**, protected in the host's own storage — connector secrets never leave the Core, so a client-side intake credential cannot come from a connector by design.

### Admin Console — Operator/Admin UI
- `Auxilia.AdminConsole` is the Blazor Server operator/admin UI and a **pure `Auxilia.Core.Client` consumer** (REST + SSE): dashboard, run surfaces, connectors, provider catalog, identity sources, audit, and **view rendering** for run outputs. It holds **no database** and no secrets — every read/write goes through the Core API. Live views arrive over the Core.Api SSE stream, so there is no view backplane.
- There is **no separate Orchestration Service**: work-item intake and trigger decisions live in the Product; pre-flight, dispatch authority, and lifecycle live in the Core (Api + Runner). A standalone orchestrator would only forward decisions and add a failure mode without an isolation benefit.

### Message Bus - IMessageBusClient
- **Default: RabbitMQ** (runs locally in Docker, zero-config)
- **Cloud swap: Azure Service Bus** (drop-in alternative via the same interface)
- Carries run requests (Core.Api → Core.Runner), status/view events, Resource-Proxy traffic, and AI-assistance requests. Workload containers reach the platform **only** through here — never a direct call — and **each with its own broker identity** (minted per run, revoked on every terminal path): it may consume only its own queues and publish only through the runner's `workflow.ingress` exchange, the terminal-state fanout, its own view routing key and non-reserved event types. Every publish carries the broker-validated publisher (`user_id`); consumers drop a workload-scoped message whose publisher is another run's identity and any platform-only message (commands, responses, lifecycle, audit) from a workload. A container can therefore never read the run-command queue, steal another run's resolution token, or speak for another run (§7).

### Workspace Manager
- Manages **warm cache** entries for each repository — cloned once, kept current via background fetches via the Resource Proxy
- On workflow dispatch: checks repository access rights per repo per source system, then creates an **isolated CoW snapshot** per repository for that run
- Mounts all declared repository snapshots into the container under `/workspace/repos/<id>/` using Linux mount namespaces — the container sees only its own snapshots
- Mounts resolved artifact inputs read-only under `/workspace/artifacts/<input-name>/` and provides the declared artifact output location, persisted to the Artifact Store on run completion
- Supports **multiple repositories from different source systems** in a single workflow run (Git, TFVC, GitHub, ADO — each using the correct Account Bundle)
- Source-control writes (commits, pushes, PR creation) happen **mid-run, directly from the workflow** through its source-control slots with just-in-time scoped credentials. Post-exit output collection by the platform was rejected: it would duplicate traffic and block legitimate in-run operations such as reviewing git history. What a workflow may do is bounded by its **declared slot capabilities** (e.g. read-only vs. write) clamped by the **operator configuration** (e.g. allowed branch patterns, PR-only, no force-push). Enforcement is layered: the issued credential is scoped to the configured limits wherever the provider supports it (the hard backstop, valid even against a compromised process), and the slot handler enforces the same limits uniformly for all providers before executing an operation
- Enforces per-repo and per-tenant storage quotas
- Sensitive repositories can be marked `no-cache` in the workflow manifest — these are fetched fresh per run and deleted immediately after

### Artifact Store - IArtifactStore
- Persistent, pluggable store where named artifacts survive between runs — the producing run's CoW snapshot is destroyed at exit, so artifacts must be persisted by the platform
- **Production:** the workflow writes its declared artifacts to a dedicated output location in its workspace; on run completion the platform persists them into the store. What a workflow may produce is limited by its manifest declarations clamped by operator configuration. The runner reads that location as the workload's — only regular files inside it, never through a link the workload planted (on Linux each file is opened `O_NOFOLLOW | O_NONBLOCK` and its real path checked through `/proc/self/fd`)
- **Checkpoints** are the one artifact kind a run produces WHILE it runs (§9 *Run checkpoints*): `ArtifactDto.Kind = "checkpoint"`, written and encrypted by the Core itself, kept for a lifetime (`ExpiresUtc`) and deletable by the run's principals — every other artifact is the platform's record and stays
- **Consumption:** declared artifact inputs are resolved at dispatch time and mounted **read-only** into the container under `/workspace/artifacts/<input-name>/` — consumption is direct from the workflow's point of view, exactly like repository snapshots
- **References, not payloads, on the bus:** messages carry only artifact references (ID + content hash); payloads never travel through the message bus
- **Identity and lineage:** an artifact is identified by artifact type + producing workflow + work item + run + version. Versions of the same lineage are ordered, enabling "latest CodeReviewResult for work item X" resolution and trend comparison against prior versions (UC7)
- **Two concerns behind one interface:** the metadata index (lineage, versions, dispatch-time queries) and payload storage are separate concerns. Backends are pluggable per deployment: local filesystem (dev), Azure Blob / S3-compatible (cloud), or a database — a database backend can hold both payload and metadata for small structured artifacts, while blob backends pair with the platform database for metadata
- Retention periods and consumption access (which workflows/identities may read which artifact types) are operator configuration, enforced at dispatch-time resolution

### Platform Data Layer - Auxilia.UniversalDataAccess
- The same `IDataAccess` / `PlatformData` abstraction backs every service, but each points it at a **separate backend instance** (distinct Mongo database / JSON root): the **Core.Api DB** (principals, roles, groups, connectors + provider catalog with secrets encrypted at rest, the audit log), the **Core.Runner DB** (run lifecycle / ownership, schemas, slot configurations, provider registrations), and the **Product DB** (workflow types, configurations, signal handlers, view data, dashboards, triggers). No service reads another's store.
- Storage backend is pluggable behind the abstraction (InMemory for tests, JSON for dev, MongoDB for replicated production). There is **no Redis/SignalR view backplane**: live views fan out over the **Core.Api SSE stream** (see sections 14–15), and runner liveness rides the `RunnerHeartbeat` bus beat plus the runner's own `ServiceHeartbeatRecord` store.
- **Transition state:** consolidating the Core.Api and Core.Runner stores into a single **shared Core DB tier** (the two Core tiers are one family) is planned; today they are separate, and Core.Api hands the runner a self-contained run spec so no shared store is required.

### Audit Log
- Append-only record of every action: human and AI operations, policy decisions (allow **and** deny), credential deliveries per slot activation, broker identities issued/revoked, Resource Proxy calls, egress (every denied attempt live, each allowed destination at first use, and a per-run summary with every destination's connection count and the SHA-256 of the complete per-attempt `egress-log` — the artifact's content hash — so every allowed and denied attempt is anchored in the chain), administrator access to someone else's run (`run.admin-access`), and workflow lifecycle transitions including retries, failovers and the egress kill switch
- Written by platform components only — workflows can neither write nor read it directly (the forwarding message is platform-only)
- **Centralised in the Core** as the single security trail (a deliberate exception to per-service databases: audit must not fragment): runners keep their own copy AND forward every record over `platform.audit`; the Core ingests them (idempotent per record id, `Source` stamped). Retention period and read access are operator configuration
- **Tamper-evident:** every writer chains its records (sequence + an HMAC over the record and its predecessor, keyed from the platform protection key). `GET /api/audit/integrity` (MCP `verify_audit`, `ICoreClient.VerifyAuditAsync`) re-verifies every chain and reports edits, deletions and insertions; without a protection key the chain is unkeyed (dev) and reports only careless edits
- Run evidence the workload cannot alter is persisted as run artifacts: the workflow container's own output (`container-log`), the egress gateway's connection log (`egress-log`), companion logs
- Verifies every workflow artifact before execution - no unsigned execution path exists
- In dev mode signing can be disabled entirely; no dev CA ceremony required locally
- Three production implementations depending on deployment mode (see section 12)
- Private key never touches the application process - only hash-in / signature-out

### Workflow Runner - IWorkflowRunner
- Abstracts how workflows are launched
- Three implementations depending on deployment mode (see section 12)

### Workflow C# SDK - NuGet Package
- Reference SDK for authoring workflows in C#; other language SDKs follow the same message contract
- Workflows declare in their manifest: required bundle types, produced artifacts, named resource dependencies, accepted artifact inputs, repository references (single or multi, multi-source), baseline network endpoints (minimum required, `package-proxy` preference), and `no-cache` flags per repository
- The manifest does **not** declare `allow-all` — that is a run-time operator decision resolved by the Policy Resolver at dispatch
- WorkflowContext carries: the originating work item, resolved named artifact inputs, scoped credentials, and repository mount paths
- Workflows compile to a self-contained executable, then are signed and pushed to the registry

### AI Integration Layer
- Routes AIAssistanceRequest messages (published by workflows) to the configured model adapter
- Pluggable adapters: GitHub Copilot, Azure OpenAI, custom models
- AI bundles carry usage quotas and purpose restrictions; pre-flight verifies the required bundle is available before dispatch
- All AI decisions are written to the audit log with full context
- See **section 5** for the full AI integration design including the MCP interface, audit contract, and bundle scoping

### Resource Proxy
- Platform-managed gateway through which workflows access external APIs, databases, build systems, and scan tools
- Workflow declares named dependencies (e.g. "github-api", "customer-db", "ci-runner") at authoring time
- Workflows are **trusted by signature**: the signing authority is responsible for verifying that a workflow is correct and trustworthy before signing it. A verified workflow is therefore permitted to hold scoped credentials directly — the protection model is *who gets credentials and when*, not hiding credentials from the workflow process
- Credentials are delivered **just-in-time**: a workflow receives a slot's credentials only at the moment it activates that slot, encrypted for that specific workflow instance — never as a blanket grant at startup, and never for slots it does not use in that run
- Supports two call patterns: **synchronous** (request/response) and **async long-running** (submit job, receive handle, poll or await callback via message bus)
- All proxy calls are audited; rate limiting and access policy enforced per workflow identity
- Calls run side by side (a bounded number per runner; past it the broker holds requests back), so one slow call — a large checkpoint — never holds up another run's. A run has at most two calls in flight (a further one is refused at once), and every call is answered within 50 s of its arrival — a wait for capacity included — so the SDK, which waits 60 s, always hears how it ended. The platform's own connector here is the checkpoint save (§9)

### Network Egress Layer
- Enforces the **effective network policy** for each run, resolved at dispatch time from manifest baseline + run configuration + platform policy ceiling, plus the run's own endpoints (its workspace hosts, its slot providers' declared `NetworkEndpoints`) and the operator's platform endpoints
- Realized by a per-run **egress gateway** (`Source/Platform/Auxilia.EgressGateway`): the workflow container's only network is the run's `--internal` network; the gateway alone also joins the outbound network, proxies HTTP(S) to allowed destinations only, relays the broker and the run's terminal, and logs every attempt. Every connection stays with the destination it was admitted for: a CONNECT tunnel's TLS server name (SNI) must name that destination — domain fronting over a shared front end is refused before a byte of the handshake leaves the gateway — and a plain request is re-issued with the target's own `Host`, one request per connection
- Default mode: **default-deny** — only allowlisted endpoints are reachable; everything else is refused, logged and audited live; external names do not even resolve inside the run network (no DNS exfiltration)
- Optional mode: **allow-all** — requested via run configuration; still proxied, logged and audited; platform policy can prohibit it entirely (and does by default)
- Build tools and package managers (`dotnet restore`, `npm install`, `pip install`, git, the agent CLIs) work through the standard proxy variables (`HTTPS_PROXY`/`HTTP_PROXY`/`NO_PROXY`, plus `NODE_USE_ENV_PROXY` for Node runtimes — system-verified for Claude Code and the Copilot CLI); a tool that ignores them has no route at all, and raw non-HTTP sockets have none either

### Package Proxy (optional)
- Platform-managed mirror of public package registries (NuGet, npm, PyPI, Cargo, Go modules, etc.)
- When enabled: the Network Egress Layer routes all package manager traffic through the proxy rather than directly to the public registry
- Provides: **caching** (packages fetched once, served locally on subsequent runs), **security scanning** (packages scanned before serving), and **air-gap support** (enterprise environments with no internet access)
- Can be configured per registry — e.g. route NuGet through the proxy but allow npm direct access
- Not required — workflows function without it as long as the registry endpoints are declared and reachable

### MCP Server
- MCP is a **first-class, authenticated** surface: the **Core MCP** (on Core.Api) exposes runtime / connector / identity / group / artifact tools. (The separate Product MCP retired with the WorkflowStudio deployable — authoring is a library call now; an authoring MCP can return as part of a host app if one needs it.)
- Every tool authenticates (API-key bearer, principal-bound) and is policy-checked against the **same authority as REST** — AI and service principals get UI parity with no hidden operations and no separately maintained mirror.
- Representative Core tools: `run_workflow`, `run_configuration`, `list_runs`, `get_run`, `cancel_run`, `list_connectors`, `create_connector`, `create_group`, `assign_group_role`.

### Integration Adapters
- Integration adapters live in **workflow-domain hosts** (the `Auxilia.Workflows.Client` family — e.g. the email intake in `Auxilia.TriggerHost`); the host owns *which* workflow a work item should run and starts it by calling the Core **Run API**, never by publishing to the bus directly. Two adapter contracts:
- **ITaskSourceAdapter** — read work items (full detail including linked items, history, attachments), update status, create work items, create sub-tasks, attach artifact references (`AttachArtifact`)
- **ISourceControlAdapter** — clone, diff, branch, commit, push, create PR; plus lightweight introspection: ListDirectory, GetFileContent, DetectFrameworks (no full clone required)

---

## 5. AI Integration Layer

AI agents are first-class citizens in Auxilia. They interact through the same interfaces as human users — via the MCP Server — and are subject to the same policy, audit, and identity rules.

```mermaid
graph TD
    subgraph AIClients["AI Clients"]
        COPILOT["GitHub Copilot"]
        AOAI["Azure OpenAI"]
        CUSTOM["Custom Models"]
    end
    subgraph Platform["Platform"]
        MCP["MCP Server"]
        AI_LAYER["AI Integration Layer"]
        WF["Running Workflow"]
        BUS["Message Bus"]
        AUDIT["Audit Log"]
        SEC["Policy Engine"]
    end
    COPILOT <-->|MCP Protocol| MCP
    AOAI <-->|MCP Protocol| MCP
    CUSTOM <-->|MCP Protocol| MCP
    MCP --> SEC
    SEC -->|Allowed| AI_LAYER
    SEC -->|Denied| BLOCK["Blocked"]
    WF -->|AIAssistanceRequest message| BUS
    BUS --> AI_LAYER
    AI_LAYER -->|Routes to configured model adapter| AIClients
    AIClients --> AI_LAYER
    AI_LAYER -->|AIAssistanceResponse message| BUS
    BUS --> WF
    AI_LAYER --> AUDIT
```

**Key properties:**
- AI agents authenticate with their own **Account Bundle** (AI bundle) — subject to the same policy checks as human users
- Workflows request AI assistance by publishing an `AIAssistanceRequest` message to the bus; they do not call the model directly
- The AI Integration Layer resolves the appropriate model adapter from the AI bundle, makes the call, and returns the response via the bus
- Multiple AI bundles of different types can be configured (e.g. Azure OpenAI for code generation, a custom model for security analysis)
- AI bundles carry **usage quotas** and **purpose restrictions** — a bundle scoped to code review cannot be used for arbitrary queries
- Every AI decision is written to the **immutable audit log** with full context: the request, the model used, the response, and the workflow step that requested it
- Pre-flight check verifies the required AI bundle is present and within quota before a workflow is dispatched
- **MCP tools** give AI agents the same operations as the human UI: trigger workflow, get status, send input, approve step, cancel, list work items — no hidden operations

---

## 6. Workflow Lifecycle

```mermaid
stateDiagram-v2
    [*] --> Received : Work item arrives
    Received --> PreFlight : Policy check passes
    PreFlight --> Queued : Signature valid, bundles available, resources reachable
    PreFlight --> PreFlightFailed : Required resource or bundle unavailable
    PreFlightFailed --> Queued : Condition resolved, requeued
    Queued --> Running : Core.Runner launches container / process
    Running --> WaitingForInput : Step requires human or AI decision
    WaitingForInput --> Running : Input received (human via UI or AI via MCP)
    Running --> Completed : All steps finished
    Running --> Failed : Unhandled error
    Running --> Cancelled : User or AI cancels
    Failed --> Queued : Retry policy if configured
    Completed --> [*]
    Cancelled --> [*]
    Failed --> [*]
```

**Key lifecycle properties:**
- Workflows are **stateful and long-running** - once started they run until Completed, Failed, or explicitly Cancelled
- A **pre-flight check** runs before any workflow is dispatched, verifying that signature, required account bundles, resource proxies, and AI accounts are all available; it also fetches and verifies the signed `*.workflow.zip` package and pre-loads the workflow schema into the schema store
- If pre-flight fails the work item is held and the user/AI is notified; it can be requeued automatically when the condition resolves (configurable)
- **Schema pre-loading**: the `WorkflowAnnouncementHandler` reads `workflow-schema.json` from the extracted ZIP package and populates `WorkflowSchemaStore` before sending the `Run` directive — no `EmitSchema` round-trip is required
- **Workflow artifacts** are declared by the workflow and configured by the user - where they are stored and how they are linked back to the work item is a per-workflow configuration
- **Workflows are not resumable (v1)**: the platform never restores a run's state — a retry after `Failed`, and failover after an orphaned Core.Runner, always means *kill and restart from scratch* (run checkpoints, §9, hand a run's saved state back to its principals; they never feed a re-dispatch). Because a failed run may already have performed external writes, every external write a workflow performs must be **idempotent or guarded** (branch-exists check, find-or-create PR, deduplicated comments) — this is part of the workflow SDK contract
- **Failures and restarts are never silent**: every failure, retry, and orphan reassignment is published as a status event and shown in the dashboard (and via MCP), so the user always sees that a run was restarted and why
- **A run says how it ended**: the SDK reports the workflow's own reason (`IRunOutcome`, e.g. `idle-timeout`) and the number of view items it published with the final state; the Core keeps both on the run record — `RunStatus.Reason` and the stream's final status frame carry the reason, the stream waits for the items before the verdict. The Core carries the reason verbatim; its vocabulary is the workflow's
- **No run waits for its operator forever**: the runner hands every launch `Workflow__MaxIdleMinutes` (`WorkflowLauncher:MaxIdleMinutes`, default 60), the cap on any session's idle timeout and the timeout of one that sets none (`WorkflowLimits.MaxIdle`) — multi-turn agent sessions end by themselves, exactly as the operator's `end` would end them, when nobody writes for that long
- **The run exists from the moment it is accepted**: the Run API writes a Core-authored **`Dispatched`** record (keyed by the command id) and publishes a `Dispatched` status event BEFORE the bus command goes out. The runner's claim event rekeys the record onto the instance id; a command no runner ever claims is failed over by the FailoverMonitor's **claim-timeout sweep** (`dispatch-never-claimed`, default 120s, one guarded re-dispatch, the stale command's resolution stash deleted so a late launch cannot resolve credentials; the sweep re-reads each candidate fresh and takes the `Failed` transition as a versioned compare-and-swap, so a claim that lands mid-sweep wins — no duplicate re-dispatch, no resurrected command row, no deleted stash). The run-tracking mirror never applies a `Dispatched` event from the bus — the row is Core-authored, so a late/re-ordered delivery cannot resurrect the command row the claim rekey deleted. "Dispatched but invisible" is impossible by construction

### Trigger model

A dispatch originates from one of five trigger sources; all of them converge on the Core **Run API**, which authorizes the request and dispatches it to the Core.Runner pool:

| Trigger | Source |
|---|---|
| **Work item event** | Integration Adapters detect work item changes (created, moved to a configured state) and publish dispatch commands |
| **Manual** | A human via the dashboard/Studio or an AI agent via the Core MCP `run_workflow` tool — both resolve to a Core Run API call |
| **Schedule** | The interval scheduler (in the `Auxilia.Workflows.Client` library, hosted e.g. by the TriggerHost) for recurring runs, e.g. nightly security scans — dispatched through the Core Run API |
| **Artifact completion** | A finished run's persisted artifact triggers a configured follow-up workflow (e.g. CodeReviewResult → Selective Fixing) — workflow chaining without coupling the workflows to each other |
| **Platform event** | A named platform event triggers a configured follow-up workflow (the `EventTriggerEngine` in `Auxilia.Workflows.Client`, over the Core's filtered event SSE stream) — see "Platform events" below |

Which triggers are active for a workflow is operator configuration, subject to the Policy Engine.

### Platform events

An **event** is a small, named fact — a string-typed vocabulary (`EventType`), never a compiled enum. Two publishers exist, and only two:

- **Workflows** declare their event types in the schema (`DeclaresEvent`, optional typed payload) and publish via the SDK's `IEventPublisher` — a direct bus publish to the **`workflow.events` topic exchange**, routing key = the (wildcard-neutralized) event type, exactly the views/artifacts trust model: the container's bus connection is the boundary. The SDK enforces a payload cap (64 KB) — large data goes to the Artifact Store and is referenced from the payload — and refuses the reserved `run.` prefix.
- **The platform itself**: `RunLifecycleEventPublisher` in Core.Api (shared queue — one node translates each transition) turns terminal `workflow.status` transitions into the reserved vocabulary `run.succeeded` / `run.failed` / `run.cancelled`, with a **deterministic event id** per (run, type) so bus re-delivery collapses idempotently. Each `run.*` event carries the run's `WorkItemId`, enriched best-effort from the Core's stored dispatch context (the `WorkItemId` context key trigger engines inject) — missing or unreadable context yields empty, never a failed translation, and never a different event id.

There is **no client-facing publish endpoint** — clients consume, never emit. The Core mirrors every event into its own store (`EventTrackingService`, retention `CoreApi:EventRetentionDays`, default 30 days) and exposes the client surface, gated `event.consume`: `GET /api/events` (catch-up shape: `createdAfterUtc` pages oldest-first) and `GET /api/events/stream` — a SERVER-SIDE-FILTERED SSE stream (eventType/workItemId) with the same selective-ingest bus bindings as the artifact stream. Consumers de-duplicate by event id; the `EventTriggerEngine` dispatches matching triggers with `EventId`/`EventType`/`WorkItemId`/`SourceRunId`/`EventPayloadJson` in the run context.

### Workflow lifetime: one-shot vs. long-living

Workflows declare their **lifetime** in the manifest:

| Lifetime | Behaviour |
|---|---|
| **one-shot** (default) | Runs exactly once for a single trigger and always shuts down afterwards — minimises credential exposure and data retention |
| **long-living** | A service-style workflow that stays running and processes many work items / events over time (e.g. a standing review agent, a queue monitor) |

Long-living workflows keep every invariant of the platform, with these adaptations:

- **Credentials still arrive just-in-time per slot activation, but carry an expiry** — a handler that needs its credential longer fetches its slot again (`ISlotConfigurationSource`, served by the run's `SlotActivator` through Core.Runner) and receives what the bound connector holds then: a token its owner replaced mid-run, or one the Core refreshed (`ProviderOAuthRefresh`). The Core answers every resolution of a live run afresh; long-lived processes never hold indefinitely valid secrets. The Copilot slot does this for the SDK session's token callback
- **Dirty-configuration handling**: when a stored configuration is changed or marked dirty, Core.Runner signals the instance to **drain and shut down**; the replacement starts with the new configuration. Upgrade to a new workflow version works the same way (drain + replace)
- **Draining is a visible lifecycle state**: a draining instance finishes in-flight work but accepts no new triggers, and is shown as `Draining` in the dashboard until it exits
- **A crash mid-drain still replaces**: if the container dies while `Draining` without reporting a terminal state, the runner waits one extra exit-grace window for the terminal message, then fails the run like any crash (token consumed, pod/workspace/output swept) **and dispatches the replacement itself** — the drain-replace contract never depends on the dying instance's last message
- **Still not resumable**: a crash or failover is a fresh restart that re-subscribes to its triggers; the idempotency obligations of section 6 apply unchanged
- **Operator opt-in**: because one-shot is the security default, deploying a long-living workflow requires explicit operator approval in the platform configuration

---

## 7. Security and Signing

```mermaid
graph TD
    subgraph Workflow Trust
        DEV["Developer submits *.workflow.zip"] --> SIGN_SVC["Trust Service signs ZIP package"]
        SIGN_SVC --> REG["Workflow Registry"]
        REG --> WR["Workflow Runner"]
        WR --> VERIFY["WorkflowPackageVerifier: RSA-PSS signature and manifest verified before execution"]
        VERIFY -->|Valid| LAUNCH["ZIP extracted; process launched from extracted content"]
        VERIFY -->|Invalid| REJECT["Execution rejected - always"]
        VERIFY -->|Dev mode - signing off| LAUNCH
    end
    subgraph Identity and Policy
        REQUEST["Any request - UI or MCP"] --> SEC["Security Service"]
        SEC --> IDP["Identity Provider (AAD / LDAP / OIDC / Local)"]
        IDP --> POLICY["Policy Engine"]
        POLICY -->|Allowed| DISPATCH["Dispatch (Core.Api → Core.Runner)"]
        POLICY -->|Denied| BLOCK["Blocked"]
    end
```

**The Workflow Registry and Trust Service above are implemented in Core.Api** (delivered 2026-07-26): workflow types are registered **permanently** (until unregistered) via `POST /api/workflow-types` with their signed package coordinate, and **runs reference the type only** — `RunRequest`/configurations carry no package URI; `RunService` resolves the coordinate from the registry and refuses non-Active types. Trust decides the status: a package signed by a **trusted publisher key** (`CoreApi:TrustedPublisherKeys`, or the platform signing key) activates immediately; anything else — an untrusted signature, or a `docker://` image with no verifiable signature — enters **Pending**. **Docker images are registered and approved by digest** (`docker://image[:tag]@sha256:…` — the registry digest, or a local build's image ID; `DockerPackageCoordinate`): an approval vouches for content and a tag can be re-pointed, so a tag-only coordinate is refused, a record stored by tag never dispatches, and new content under an old tag is a new registration that stays Pending — no run starts — until approved. Every runner refuses tag-only coordinates and starts exactly the pinned content (`ResolvePinnedImageAsync`: the image carrying that registry digest, pulled by digest when absent, or the local image with that ID — whatever the tag points to now never runs). A Pending registration waits until the signing authority (`workflow-type.sign`, Administrator-only; operators only register) approves or **denies with a recorded reason**. A package may also be **transferred to the Core** (base64 upload): it is stored Core-side, served to the runner via a per-dispatch token-authorized URL (`core://` coordinate), and on approval **re-signed with the platform signing key** (`CoreApi:SigningKeyPemFile`) so the platform becomes the publisher of record. Pending registrations run through a **pluggable approval pipeline** (`CoreApi:ApprovalHandlers`, a DI-bound name-keyed handler catalog — the email notifier and the `verdict-workflow` handler); all-defer leaves the decision to a human. The verdict handler passes the registration's coordinates as run context; for a `core://`-stored pending package it additionally mints a **scoped download token** (bound to that one type AND that submission's package hash, valid for the evaluation window) and passes `approval-package-download-url` — the package endpoint honors it only while the type is still Pending with that same package, so the review can fetch exactly what it reviews. **A verdict lands only on the submission it reviewed**: every registration carries a `PackageHashBase64` (package bytes or docker coordinate + declared schema) and the pipeline approves/denies through `ApproveReviewedAsync`/`DenyReviewedAsync`, which the registry refuses unless the record is still Pending with the reviewed hash and `UpdatedUtc` — a re-registration during the review never inherits the old verdict, and a late pipeline Approve never overrides a human Deny. Human approve/deny likewise refuse anything not Pending (REST 409; 404 only for an unregistered type). Operator-controlled deployments seed Active types from host configuration (`CoreApi:StaticWorkflowTypes`) — held to the same content rule (a tag-only image stops the Core from starting); a type the configuration seeded follows its configured digest (audited `workflow-type.reseeded`), a type approved through the registry is never overwritten by configuration. Runtime schema announcements from the runner **refresh** a registered type's schema but never create catalog entries — registration is the only way in. Everything is audited (`workflow-type.register` / `workflow-type.sign`).

**Key principles:**
- Signing can be **fully disabled in dev mode** - there is no ceremony, no dev CA required locally
- In all non-dev modes signing is mandatory with no bypass; the signed artifact records what the workflow *is* — permissions and network policy are resolved at runtime, not locked into the artifact
- **The signature is the basis for credential delivery**: the signing authority is responsible for ensuring a workflow is correct and trustworthy. Only a signature-verified workflow may receive credentials, and it receives them just-in-time per slot activation — encrypted for the specific instance, scoped to the slot's declared capabilities, and never before the slot is actually needed
- **The resolution token is never stored in the clear**: the Core keeps only its SHA-256 digest plus a redacted dispatch-command copy (a failover/rerun re-dispatch mints a fresh token and package URL) — enough to verify what the runner presents, never to replay it — and the runner protects its own persisted copy with its settings protector. **The token is a capability of a live run**: once the run is terminal every token-authorized read (slot resolution, package and environment-layer downloads) is refused and audited, while the stash stays for rerun
- **Inline secrets are protected like connector secrets**: a binding's inline settings of `Secret`-kind provider descriptors are encrypted the moment they enter the Core (configuration write or inline run request), masked on every read (key present, value empty; an empty value on update keeps the stored one), and decrypted only inside the credential resolver straight into the run's RSA envelope
- **The registration handshake is authenticated**: channel encryption alone does not authenticate the requester. Core.Runner injects a **one-time instance token** into the container/process at launch and pre-creates an **exclusive response queue** per instance; announcements, registration requests, and terminal state reports must present the token (validated, **bound to the workflow type it was issued for**, single-use for registration, time-limited until registered), and directives/configurations are only ever delivered to that pre-created queue — the request's self-declared response topic is ignored. A terminal state report is additionally acted on only by the runner that owns the run. Token-less operation exists only behind the `RequireInstanceToken=false` setting for trusted-operator dev scenarios
- Workflows have **no arbitrary outbound network access** — effective network policy is resolved at dispatch from three layers (manifest baseline, run configuration, platform policy ceiling) and enforced by the run's egress gateway, the container's only route out; see section 10
- **A signature vouches for the program, not for what an agent it hosts will do** — containment (below) assumes the workload may be steered by untrusted input and holds regardless
- Structured API calls (task sources, source control, databases) go through the Resource Proxy; build toolchain calls (package restore, etc.) go through the Network Egress Layer
- Every action (human or AI) is written to an immutable audit log
- TFVC / TFS is explicitly supported as a source control target for enterprises that have not yet migrated

### Workload containment (2026-10-15)

A signed workflow may host an AI agent that runs arbitrary commands and can be steered by
untrusted content (repository files, work items, web pages). Signature trust therefore never
substitutes for containment: every mechanism below holds even if the workload actively tries to
break out, and each one is general — none knows a provider, a workflow, or an agent product.

```mermaid
graph LR
    subgraph RunNet["run network (--internal)"]
        subgraph WF["workflow container (caps dropped, no-new-privileges, limits)"]
            SDK["SDK process (root): bus identity, instance token, push credential"]
            AG["agent / setup scripts / workspace git (uid auxilia-agent, scrubbed env)"]
            PG["push gateway (loopback) - per-action policy"]
        end
        GW["egress gateway - allowlist proxy, broker + terminal relay, connection log"]
    end
    AG -->|git push/fetch| PG
    PG -->|via proxy| GW
    AG -->|HTTPS_PROXY| GW
    SDK -->|AMQP via relay, own broker identity| GW
    GW -->|allowlisted only| OUT["outside world"]
    GW --> BUS["broker: ingress exchange + own queues only"]
```

| Concern | Mechanism |
|---|---|
| Bus | A per-run broker identity (`WorkloadBusAccess`, minted by the runner over the RabbitMQ management API, revoked on every terminal path): own queues + the ingress exchange only. Publisher attribution (`user_id`) drops forged workload-scoped messages and any platform-only message from a workload — the run-command queue, other runs' tokens and views are unreachable |
| Network | Per-run egress gateway; allowlist = policy + the run's own endpoints (workspace hosts, slot providers' declared `NetworkEndpoints`) + platform endpoints; loopback/link-local/metadata addresses never reachable through a name; private/internal addresses (where the platform's services and other runs live) only through an explicit entry — allow-all means the public internet; the broker relay admits only the run's own AMQP identity (its login is inspected before it reaches the broker); a tunnel's TLS server name must name its admitted destination (no domain fronting) and a plain request carries the target's own `Host`; every attempt logged, denials audited live, the complete log anchored in the audit chain by its digest; optional kill switch (`EgressDenialLimit`) fails the run as a breakout attempt |
| Terminal | A per-run terminal credential that ttyd requires and only the Core's authenticated proxy presents (Core-internal endpoint userinfo); the terminal is relayed by the run's gateway, never published from the workflow container |
| Signed artifacts | One runner trust list (`WorkflowDispatcher:TrustedSigningKeys`) for environment fragments, workflow packages and slot plugins; packages carrying files their signed manifest does not list are refused |
| Kernel / host | `--cap-drop ALL` + `CHOWN DAC_OVERRIDE FOWNER SETUID SETGID KILL` (operator-tunable), `no-new-privileges`, default seccomp/AppArmor, pids/memory/CPU ceilings (no swap beyond the memory ceiling — the kernel stops the container AT its limit and the run fails saying so; the CPU ceiling, 4 by default, is clamped to the daemon's cores); a **read-only root filesystem** — writable only through the run's binds, tmpfs scratch (`/tmp`, `/var/tmp`, `/run`, the SDK's private `/root`, its wrapper directory) and anonymous per-container volumes (the agent's home, a baked image's application directory the runner injects slot plugins into), so the image stays exactly what was approved for the whole run; no Docker socket, never privileged; the host-gateway mapping is off unless an operator opts in. **Second kernel boundary:** workflow containers and companions run under a sandboxed OCI runtime whenever the daemon offers one — the configured `ContainerRuntime`, else the first of `PreferredSandboxRuntimes` (default `runsc`, `kata-runtime`) — and a runner in the production posture (`TrustedSigningKeys` set) refuses any launch on the shared host kernel (`RequireSandboxRuntime`, defaulting to the posture; only an explicit `false` opts out) |
| Daemon | The runner reaches Docker only through the **Docker API guard** (`Source/Platform/Auxilia.DockerGuard`, which alone mounts the socket): exactly the calls a runner makes; every container it creates carries an ownership label and the confinement above (no-new-privileges and no other security option, never privileged, capabilities it ENDS UP with — Docker's defaults minus its drops plus its adds, read as the daemon reads them — within the runner's set, the daemon's default runtime or one the operator lists, no host namespace/device/sysctl/unmasked path, owned volumes without driver options, owned internal bridge networks joined by name only, ports on loopback literals only); host directories only below the run roots, carried out as subpath mounts of a guard-owned root volume so the daemon resolves them beneath the root (a symlink planted in a run directory cannot point a mount at the host — Docker 26+); operations only on owned objects (the daemon's answer, not the caller's); builds and deletions only of composed environments; no imports; no exec/attach/commit — bodies, paths and queries are read exactly as the daemon reads them |
| Runner posture | A daemon that cannot enforce egress (Windows containers) runs nothing unless the operator sets `AllowUnenforcedEgress` (then allow-all runs only); every runner runs `docker://` coordinates only when pinned by digest (`image@sha256:…`) and starts exactly that content — a re-pointed tag never runs (the registry approves images by digest, §7 above) |
| Process | `AgentSandbox`: agent CLIs, their terminal sessions, workspace setup scripts and workspace git run as the unprivileged `auxilia-agent` user (uid/gid 10001, provisioned by the image at build time — the root filesystem is read-only at run time; a root SDK that cannot establish the sandbox refuses to start instead of running agent code as root) with every platform secret scrubbed — the SDK removes them from its own environment before any workload code starts. A different uid cannot read the SDK's memory or environment. The workspace, output and pod volumes are the agent's, SHARED with the SDK through the agent group — the SDK process takes it as its effective group with umask 002 (root's own access is unchanged; its private state stays under 0700 `/root`, its launcher wrappers are explicitly root-only-writable), so files the workflow prepares there stay writable for its agent on every filesystem, host shares included; agent CLI state (credentials, hook and MCP settings) is prepared in the agent's own home. No agent process outlives its session: when the last agent session ends, every process of the agent user is killed |
| Credentials | Push credentials never enter the workspace: the SDK's loopback **push gateway** serves the remote, and every push — parsed to its exact ref updates — goes through the per-action policy point (`ActionGate`, kind `git.push`): the agent session's policy (auto / ask the operator / deny when there is no operator), the workflow's own code otherwise. Obfuscating a command or calling a provider API directly gains nothing — the agent holds no push credential |
| Clone sources | Transport allowlist (`AllowedCloneTransports`, default https/http/ssh/git; never `ext`/`fd`), option-injection proof argv (`--`), safe mount ids and branch names, `protocol.file.allow=never` in every workspace clone |
| Evidence | Central, chained audit (§4) anchored outside its store: after EVERY stored record the Core appends a signed checkpoint of its chain head to `CoreApi:AuditCheckpointPath`, and the integrity check reports a chain whose checkpointed head is missing or replaced — cutting even the last record from the store is detected; `container-log` and `egress-log` artifacts per run |

Per-action policies are a general mechanism: an enforcement point raises a `GatedAction`
(open-vocabulary kind, target, what it concretely does); an agent session maps each kind to its
policy. `git.push` is the first kind — deploys, mail or ticket writes plug into the same point.

Boundaries of the model (each with its control): the agent holds the credential for its OWN
model API — the trust model's deliberate choice (scoped, just-in-time, per slot), reachable only
on allowlisted endpoints; the second kernel boundary needs a sandboxed runtime installed on the
Docker host — used automatically when present, mandatory in the production posture; the audit
anchor is as strong as the storage behind `AuditCheckpointPath` — write-once storage protects the
anchor itself; the bus between the Core and its runners (run commands carry resolution tokens) is
a platform trust zone no workload identity can read; the SDK process stays root INSIDE its
container (it needs `CAP_SETUID` to drop agent code to its own uid and keep its secrets from it)
— on the host that root is unprivileged under a sandboxed runtime or daemon user-namespace
remapping, while a plain runc host shares root's uid; and the Docker daemon is reached through the
**Docker API guard** (`Auxilia.DockerGuard`): it alone holds the socket and lets the runner do
exactly what a runner does — owned, confined containers, owned networks and volumes, host
directories only beneath the run roots (resolved there by the daemon, so a planted symlink
leads nowhere), builds of composed environments, nothing on anyone else's objects, no exec — so
even a compromised runner process cannot turn the daemon into host root.

**Environment layers share the workflow trust bar** (delivered 2026-07-27; multi-base variants 2026-08-15): a session environment is an admin-managed Core record — a capability name carrying **one initialization-script variant per base environment it supports** (`linux`/`windows`, each with an optional base-version pin, + an optional layer-wide software version) — managed via `/api/environment-layers` (gated by `provider-catalog.manage`; the upsert is keyed (providerType, baseEnvironment) and also maintains the available provider-catalog entry, category `environment`; `DELETE …/variants/{base}` removes one variant, the last removal removes the layer). Because the script executes at image-build time, the Core generates the build fragment — a base64-wrapped `sh -e` RUN layer on linux, a base64-wrapped stop-on-error PowerShell RUN layer on windows — and **signs it with the platform signing key**; the runner fetches the variant matching **its own base** at dispatch (`…/content?base=<os>`, resolution-token authorized, the base taken from the same Docker-daemon probe that feeds the heartbeat) and verifies it against `WorkflowDispatcher:TrustedSigningKeys` (the runner's one publisher-trust list, also applied to workflow packages and slot plugins) before composing the content-addressed `auxilia-env:<hash>` image (hash includes the base image digest, so a rebuilt workflow image never reuses a stale composition). An empty trust-key list is the dev-host posture, exactly like disabled package signing; a statically configured local layer of the same name is the host's override. **One run composes one image on one base**: each layer's supported base set rides its provider-catalog entry (`EnvironmentBases`), and a dispatch whose selected layers share no base (or whose version pins disagree on every shared base) fails fast at the Core — a layer with both a linux and a windows variant composes with either side.

---

## 8. Resource Access Model

Workflows interact with external systems through two distinct channels depending on the nature of the call.

```mermaid
graph TB
    subgraph Container["Workflow Container (own network and mount namespace)"]
        WF["Workflow / AI Agent"]
        BUILD["Build Tools (dotnet, npm, pip...)"]
    end

    subgraph StructuredAccess["Structured Access - Resource Proxy"]
        PROXY["Resource Proxy"]
        ACCT["Account Manager"]
        AUDIT1["Audit Log"]
    end

    subgraph NetworkAccess["Network Access - Egress Layer"]
        NET["Network Egress Layer"]
        PKG_PROXY["Package Proxy (optional)"]
        AUDIT2["Audit Log (all traffic including blocked)"]
    end

    EXT["External APIs / Databases / CI systems"]
    PKG_REG["Package Registries (NuGet, npm, PyPI...)"]
    BLOCKED["Undeclared endpoints - blocked"]

    WF -->|Named resource request via message bus| PROXY
    PROXY --> ACCT
    PROXY --> AUDIT1
    PROXY -->|Scoped credentialed call| EXT
    EXT --> PROXY
    PROXY -->|Response via message bus| WF

    BUILD -->|package restore, build commands| NET
    WF -->|declared direct network calls| NET
    NET -->|declared endpoint| PKG_PROXY
    NET -->|declared endpoint| EXT
    NET -->|undeclared endpoint| BLOCKED
    NET --> AUDIT2
    PKG_PROXY --> PKG_REG
```

**Resource Proxy (structured calls):**
- Used for all calls where the platform mediates credentials: task sources, databases, CI triggers. (Source-control PUSH: the push credential reaches only the SDK process, whose loopback push gateway serves the workspace and puts every push in front of the per-action policy — §7 *Workload containment*, §9 write-back control.)
- Workflow declares named dependencies in its manifest; at runtime the platform resolves the correct Account Bundle
- Supports synchronous and async long-running patterns (trigger + poll/callback via message bus)
- The workflow holds no credentials **prior to slot activation**. Because every workflow is signature-verified before launch, it is trusted to receive scoped credentials — but only just-in-time, per slot, encrypted for that workflow instance, and only for the slots it actually activates during the run

**Network Egress Layer (toolchain and direct calls):**
- Used for build tools, package managers, and any other direct network calls the workflow needs
- The manifest declares allowed endpoints and their purpose; enforcement is the run's egress gateway, the container's only route out
- Build tools work through the standard proxy variables — `dotnet restore` reaches the declared NuGet endpoint with no per-tool abstraction
- Every attempt is logged by the gateway; denied attempts are audited the moment they happen

**Network policy modes:**

| Mode | Behaviour | When to use |
|---|---|---|
| **default-deny** (default) | Only declared endpoints are reachable; all else blocked and logged | All production workflows |
| **allow-all** | All outbound traffic permitted; everything still logged | Requested via run configuration or global tenant config; platform policy can prohibit entirely |

---

## 9. Workspace Management

Workflows operate on local filesystem copies of repositories rather than streaming content through the message bus. The **Workspace Manager** manages this entirely on behalf of the workflow.

**Workspace mounts are generic slot bindings.** A repository is not a special contract shape: it is a `SlotBinding` of a catalog provider that declares `MountsIntoWorkspace` (e.g. `git-repository`), with inline non-secret settings and an optional credential connector (linked through the provider's `RequiredCredentialContract`, e.g. `git-credential` implemented by `github-account`/`tfs-account`). At dispatch the Core re-keys the binding's settings by the provider's declared setting **roles** — a pure data transform; the Core never interprets the role vocabulary — and ships `WorkspaceMountDispatch` items. Only the execution plane defines what the roles mean (`WorkspaceMountRoles`: clone source, branch, working directory, cache policy, push opt-in, commit identity); the mount's credential is stashed under a synthetic `mount-auth:` slot and resolved just-in-time. Every clone gets a provisioned commit identity (binding settings or the platform default) — agents never guess an authorship. A slot declaring `AllowMultiple` binds several repositories in one run; each mount's effective root is announced to the container as `Workflow__WorkspaceMount__<ID>`. Editors render mount forms entirely from the catalog descriptors (`Role`, `Browse`, `BrowseDependsOn` drive live repository/branch pickers over the generic connector-browse endpoint)); the browse itself is equally data-driven: a provider's catalog registration declares `ProviderBrowseSpec`s per browse kind (URL template with `{placeholder}`s from connector settings/context, a declared auth style — bearer or PAT basic — accept headers, response field paths, and an optional resolve pre-step such as clone-URL to repository-id), and the Core executes the spec generically — like `ProviderOAuthRefresh` and `ProviderModelCatalog`, the vendor knowledge lives in the registration payload, never in Core code. A provider without a spec for the requested kind surfaces as not-supported and clients fall back to manual entry.

```mermaid
graph TB
    subgraph WorkspaceManager["Workspace Manager (privileged host process)"]
        CACHE["Warm Cache\n(one entry per repo, never mounted into containers)"]
        COW["CoW Snapshot Engine"]
        ACCESS["Access Check (per repo per run)"]
    end

    subgraph RunA["Container A - own mount namespace"]
        MNT_A["/workspace/repos/main-app\n/workspace/repos/shared-lib"]
        WF_A["Workflow Agent A"]
    end

    subgraph RunB["Container B - own mount namespace"]
        MNT_B["/workspace/repos/legacy-module"]
        WF_B["Workflow Agent B"]
    end

    PROXY["Resource Proxy"]
    SC1["GitHub (main-app)"]
    SC2["Azure DevOps (shared-lib)"]
    SC3["TFS (legacy-module)"]
    POLICY["Policy Engine"]

    PROXY -->|fetch via Account Bundle| SC1
    PROXY -->|fetch via Account Bundle| SC2
    PROXY -->|fetch via Account Bundle| SC3
    SC1 --> CACHE
    SC2 --> CACHE
    SC3 --> CACHE

    ACCESS --> POLICY
    POLICY -->|granted| COW
    POLICY -->|denied| REJECT["Snapshot refused - run blocked"]
    CACHE -->|CoW fork| COW
    COW -->|bind-mount into namespace| MNT_A
    COW -->|bind-mount into namespace| MNT_B
    MNT_A --> WF_A
    MNT_B --> WF_B
    WF_A -.->|cannot reach| MNT_B
    WF_B -.->|cannot reach| MNT_A
    WF_A -.->|cannot reach| CACHE
```

**First-class workspace resources (2026-08-03).** A workspace source — usually a repository,
but deliberately named for what it IS: any workspace-mount source, including future
non-repository ones (a temporary scratch workspace, a share) — can be saved once as a Core
resource (`/api/workspaces`): a workspace-mount provider's NON-secret settings (clone URL,
branch, working directory, cache/push policy, commit identity, setup script) plus an optional
credential-connector reference, shared exactly like connectors (Personal owner + grants, or
Company). A configuration's slot binding references it by id (`SlotBinding.WorkspaceId`);
dispatch expands the reference LIVE — the resource supplies provider type, settings, and
connector, binding-level values override per use, and access is gated like connectors — so
editing the workspace once (a new setup script, a branch move) applies to every configuration
referencing it. Settings are non-secret and round-trip on reads; the credential never leaves
the connector. The workspace ids a run expanded from are stashed with the run, so a rerun
re-gates each one against the rerunning principal (like connectors and, for a personal
configuration's run, the configuration's visibility).

**Post-binding setup scripts (2026-08-03).** A repository may need repo-specific setup
(restore, codegen, bootstrap) before the workflow can act on it. Two sources, one executor:
a manifest-declared script (`RequiresRepository(..., setupScript:)` — part of the signed
package, so signature trust covers it) and a mount-bound `setup-script` setting role (whoever
may bind the mount already controls the repository content the run acts on, so the script adds
no authority; the runner only *announces* it as `Workflow__WorkspaceMountSetup__<ID>`, never
executes it). The SDK runs every script INSIDE the workflow container — in the mount's root,
after the workspace is bound, before the application, under the run's egress policy — and a
non-zero exit fails the run fail-fast.

**Empty scratch workspaces (2026-08-03).** The first non-git workspace provider:
`empty-workspace` (a data-only catalog descriptor, no Core code) materializes a fresh, empty
scratch directory per run under the same `/workspace/repos/<mount-id>/` layout — no clone, no
credential, same roles pipeline (a bound `working-directory` is pre-created host-side so the
announced root exists; a bound `setup-script` is announced as usual). The runner selects the
materializer from the roles a mount carries: a mount WITH a `clone-url` role is materialized
by git; one without it (and without a credential — a credentialed mount lacking a clone source
fails pre-flight as a misconfiguration) becomes an empty scratch directory. Like every mount
it lives in the run root and is deleted at terminal state.

**Multi-source repository support:**
- A workflow manifest declares all required repositories with their source system and identifier
- Each repository is fetched and cached independently using the correct Account Bundle for its source
- All declared repos are mounted into the container under `/workspace/repos/<manifest-id>/`
- Access is checked independently per repository — a workflow only gets snapshots for repos it is authorised to access

**Isolation layers:**

| Layer | Mechanism |
|---|---|
| Run-to-run isolation | Each run gets its own CoW snapshot; writes do not affect other runs or the warm cache |
| Cross-run filesystem | Linux mount namespaces — a container can only see its own mounted snapshots |
| UID isolation | Workload-controlled processes run as the unprivileged `auxilia-agent` user, separated from the SDK's root identity inside the container (§7 *Workload containment*); runs are separated from each other by their containers, networks and broker identities |
| Warm cache protection | Cache directory is host-only, owned by the Workspace Manager, never bind-mounted into any container |
| Write-back control | A binding with `AllowPush` gets a fresh clone whose configuration holds **no credential**. The push remote (the connector's optional `push-token` — e.g. a fine-grained PAT limited to that repository — or else the clone credential) is handed to the SDK process ONLY (`Workflow__PushRemote__<ID>`, scrubbed before workload code starts); the SDK's loopback **push gateway** serves the workspace's `origin`, relays fetches, and decides every push from its parsed ref updates through the per-action policy point (`ActionGate`, kind `git.push`): the agent session's `push-policy` (ask / auto, live-changeable; ask with no operator = deny) — or, outside agent sessions, the workflow's own code. Superseded 2026-10-15: the credential-in-clone Model B and its command-string interception |
| Clone sources | Runner-side git uses only the configured transports (`WorkflowDispatcher:AllowedCloneTransports`, default `https, http, ssh, git`; `file` only by explicit opt-in; `ext`/`fd` never), `--` before every clone source, and validated mount ids / branch names; every clone handed to a workload sets `protocol.file.allow=never` |

**Environment capabilities (2026-07-27).** A run's container environment is COMPOSED from
capability selections, bound like everything else: catalog entries declaring
`ComposesEnvironment` (category `environment`, runtime-extensible — no compiled enums) are
offered on an optional multi-binding `environment` slot; the Core routes the selections
generically (`EnvironmentCapabilities` on the run command, opaque ids); the RUNNER owns the
vocabulary — capability → Dockerfile fragment (`WorkflowLauncher__EnvironmentLayers`) — and
layers the fragments onto the workflow image, content-addressed (`auxilia-env:<hash>`) so each
distinct combination builds once and later runs cache-hit. An unknown capability fails
pre-flight. A "template" is simply a capability whose fragment installs a whole stack.

**Versioned bases (2026-08-03) and multi-base variants (2026-08-15).** A layer names a
capability; per supported base ("linux", "windows") it carries its own setup-script VARIANT
with an optional pinned VERSION from the admin-managed **environment-base catalog**
(`/api/environment-bases`: (name, version) pairs like ("linux", "ubuntu-24.04") — an open,
configurable vocabulary, provider-catalog-gated like layers). A pinned version must be
registered (a typo'd pin would silently never compose); an unpinned variant composes with any
version of its base. The supported base set (with pins) rides the layer's catalog entry
(`EnvironmentBases`), and both the dispatch (`RunService`) and the authoring mirror
(`WorkflowAuthoring`) fail fast when the selected layers share no base whose version pins
agree — one run composes one image on one base, but a multi-variant layer composes on any of
its bases.

**Fleet platform advertisement + base seeding (2026-08-08).** Each runner probes its Docker
daemon once (OS type + architecture — the daemon's answer, not the process OS: the socket may
be remote) and advertises the result on every `RunnerHeartbeat`. The Core's liveness tracker
keeps it per runner and serves the fleet over `GET /api/runners` (gated
`provider-catalog.manage`), so environment editors can tell which base NAMES the connected
fleet actually hosts. The AdminConsole offers a small CURATED seed list when the base catalog
is empty (one-click add, filtered by the fleet's advertised platforms; the admin stays the
curator) — deliberately no registry lookup: bases are an abstract compatibility vocabulary,
and a Core-side registry call would break registry neutrality and air-gapped deployments.

**Warm cache behaviour:**
- Cache entries are populated on first clone and refreshed on every reuse: an authenticated `git fetch --prune`, then the checkout is advanced to the fetched tip of the run's requested (or the origin's default) branch, so every run starts on the current commit. A FAILED fetch fails the run's preparation (`PreFlightFailed` with the reason) — a run never proceeds on a copy that may be outdated. The refresh, the checkout and the run's copy of one entry are serialized (concurrent runs of one repository never copy a tree another run is checking out), and a first clone lands in a staging directory moved into place only when complete
- Entries are keyed by the credential-STRIPPED clone URL (a token rotation reuses the entry); the credential is injected per git command as `-c url.<tokened>.insteadOf=<stripped>` and never persisted in any `.git/config` — cache or per-run clone, push-enabled ones included (their push credential lives only in the SDK's push gateway; system-proven: no workspace file holds the token, and the push still lands)
- A cache entry may only be used to create a snapshot if the requesting identity independently passes an access check for that repository
- Repositories marked `no-cache` in the manifest are fetched fresh per run and deleted immediately after — for high-sensitivity codebases
- Per-tenant cache storage is physically separate in multi-tenant deployments, encrypted at rest using tenant-specific keys

**Run input files (2026-11-08).** A workflow may declare files it takes per run (`RequiresInputFile(name, …, required)` — a schema field, so the Core knows them before a type's first run). A caller uploads each one (`POST /api/run-input-files`, the body is the file, capped by `CoreApi:MaxRunInputFileBytes`) and names the upload in its run request (`RunRequest.InputFiles`: declared name → upload id):
- **Held encrypted, the uploader's alone.** The content lives beside the artifact payloads (shared by every Core node), encrypted with the platform protection key; until a run takes it, only its uploader can name it — anyone else's upload is indistinguishable from a missing one — and it is deleted unused after `CoreApi:RunInputFileTtlMinutes`
- **One run.** The dispatch binds each upload to that run's command (compare-and-swap: an upload binds once) after checking the type's declarations — undeclared names, missing required files and reused uploads are refused. Only the NAMES ride the dispatch command; the runner fetches each file just-in-time with the run's resolution token (`/internal/runs/{id}/input-files/{name}`, refused once the run has ended, audited) into the run's own output bind (`/workflow-output/inputs/<name>`, `Workflow__InputFile__<NAME>`, `IRunInputFiles` in the SDK) before launch. The content never rides the bus and appears in no view, input or log
- **Gone with the run.** Deleted shortly after the run ended — after a grace in which a failover re-dispatch takes the run's files over (it continues the same run); an operator rerun is a new run and gets new uploads, so a rerun of a run that had input files is refused

**Run checkpoints (2026-11-08).** A workflow may declare files or folders it saves WHILE it runs (`DeclaresCheckpoint(name, relativePath, description, lifetime)` — a schema field) and save one at any moment (`IRunCheckpoints.SaveAsync(name)`, e.g. after every turn of a session and at its end):
- **Only the name travels.** The save is a call through the audited Resource Proxy that names a declared checkpoint; the runner takes its path from the declaration and reads it from the run's output directory the way it reads outputs — regular files and directories only, each checked by its real path, never through a link (a link to the runner's own `/proc/self/environ` is left out, system-proven), a FIFO never blocks it, and every file exactly as it was when opened (a file the workload appends to meanwhile is cut there; one that shrinks fails the save rather than corrupting it) — a folder as a `.tar.gz` of its tree (`MediaType` `application/gzip`), capped by `WorkflowDispatcher:MaxCheckpointBytes` (content plus an allowance per archive entry) — and hands it to the Core with the run's resolution token (`POST /internal/runs/{id}/checkpoints/{name}`: only for the instance the run's claim recorded — earlier is a 409 —, refused once the run has ended, capped again by `CoreApi:MaxCheckpointBytes`, rate-limited per run by `CoreApi:CheckpointSavesPerMinute`, audited `run.checkpoint.saved`)
- **The latest copy, whatever the ending.** The Core keeps each save as the next version of the run's artifact of that name (kind `checkpoint`) and deletes the version it replaces only once the new one is stored, so a crash, a failure or a cancel keeps the last good save; saves that raced each other converge on the newest. It is encrypted at rest with the Core's protection key (`PlatformData:ProtectionKeyBase64` — a Core without one keeps it, like every secret, in the clear and warns at start), in the Core's own storage beside the artifact payloads every node shares (`run-checkpoints`; a payload becomes readable only once its record exists and goes before its record, so none is ever left without one), served decrypted only to callers who may reach the run (`GET /api/artifacts/{id}/content`, audited) and only as the content its recorded digest names — never past its lifetime, even before the sweep — announced on the artifact stream, and never fires an artifact trigger: chaining follows finished outputs, not a run's copy of itself. A Core node holds at most `CoreApi:MaxConcurrentCheckpointTransfers` payloads in memory at once. A failover re-dispatch is a run record of its own: its saves start afresh, and the original run's last save stays with that run
- **Lifetime, most specific level first.** Kept for its lifetime after the latest save: the run request's (`RunRequest.CheckpointLifetimeHours`) over the workflow's declaration over the general one (`artifacts.checkpoint-lifetime-hours`, else `CoreApi:CheckpointLifetimeHours`, default a week). The platform settings decide how far that goes: `artifacts.checkpoint-lifetime-overrides` = `run` (default) / `workflow` (run requests may not set one) / `none` (only the general lifetime applies), and a cap bounds every level — `artifacts.checkpoint-lifetime-max-hours`, else `CoreApi:CheckpointLifetimeMaxHours` (default 720 h, thirty days; 0 = none). A run request above the cap, or one the platform does not let runs make, is refused (400); a workflow's declaration or the general lifetime above it is cut to it. The settings in force at each save decide, and the sweep also deletes copies older than a cap lowered later
- **Handed back, then deleted.** The run's principals download it like any of its artifacts; the principal it runs for, its starter and administrators may delete it once they have it (`DELETE /api/artifacts/{id}`, MCP `delete_artifact`, `ICoreClient.DeleteArtifactAsync`, audited `artifact.checkpoint-deleted`) — grantees only read, and every other artifact kind is refused (409). Clearing the run history keeps a run that still holds checkpoints (its principals reach them through it) until they are deleted or expire

**Tool versions (2026-11-08). The tools an image provides, a slot provider requires and a run requires share one vocabulary: `name` or `name@version` (`ToolProvisions` in the contracts). A requirement without a version is met by the tool in any version; one with a version only by exactly that version. Providers' requirements gate bindings at dispatch; a run's own `RunRequest.RequiredTools` ride the dispatch command and are checked by the runner before launch against the image's provided tools — a mismatch ends the run `PreFlightFailed` with both sides named, never a run on another version. An image records the versions it bundles at build time (`ProvidedTool.Versioned`, from an assembly metadata entry its build writes).

---

## 10. Network Isolation Model

The container network model separates three categories of outbound communication and handles each appropriately. Enforcement (2026-10-15): every run on a Linux daemon gets its own `--internal` network and an **egress gateway** — the only container on it that also joins the operator's outbound network (`WorkflowLauncher:NetworkName`). The gateway (image `WorkflowLauncher:EgressGatewayImage`) receives the run's effective allowlist as data (`*` for allow-all — the public internet; private and internal addresses always need an explicit entry), proxies HTTP(S) (CONNECT and absolute-form) to allowlisted destinations only — holding each connection to the destination it admitted: a tunnel carrying TLS must name that destination in its ClientHello's server name (read before a byte is forwarded; fragmented records are reassembled, a malformed handshake is refused, a tunnel naming another server is refused and audited as a denial), and a plain request is re-issued in origin form with the target's own `Host`, hop-by-hop headers dropped, one request per connection (exactly its declared body is forwarded; a pipelined second request never is) — relays the broker (`egress:5672`, admitting only the run's own AMQP identity) and the run's terminal, refuses special-purpose addresses reached through a name, and writes one JSON line per attempt. The runner follows that log live: every denial is audited immediately, each allowed destination at its first use, and past `WorkflowDispatcher:EgressDenialLimit` the run is terminated as a breakout attempt. The log becomes the run's `egress-log` artifact, and the run's audited `workflow.egress.summary` carries every destination's connection count and the log's SHA-256 (the artifact's content hash) — every allowed and denied attempt is thereby anchored in the tamper-evident chain without one audit record per connection. Effective allowlist = manifest baseline + run configuration (`NetworkAllow`) + the run's workspace hosts + its slot providers' declared `NetworkEndpoints` − platform `BlockedEndpoints` + `PlatformEgressEndpoints`. **Protected endpoints** — the broker's AMQP endpoint (reachable only through its identity-pinned relay) and the operator's `WorkflowLauncher:ProtectedEndpoints` (e.g. the broker's management API, the Core, a database) — are refused by the gateway before any allow entry, by name AND resolved address, while other ports of the same host stay usable (a dev git server beside the broker on `host.docker.internal`).

**Raw TCP endpoints.** Any endpoint source may declare `tcp://host:port` for a protocol that is not HTTP (mail, databases, directory services). It is relayed transparently in every mode: the gateway listens on that port, the host's name resolves to the gateway *inside the workload only* (host-file entry; the gateway itself resolves the real host), so clients need no proxy support and TLS names stay valid; the relay applies the same special-purpose address rules and logs every connection like the proxy. Hosts must be names (an address cannot be redirected), one host per port, and the platform's own relay ports are reserved — a plan that violates this fails pre-flight. Allow-all therefore means the public internet over HTTP(S) plus declared TCP endpoints; undeclared raw TCP has no route.

**Endpoints from a provider's settings.** A slot plugin's manifest may declare endpoint *templates* over its own settings — the email provider declares `tcp://{ImapHost}:{ImapPort}` and `tcp://{SmtpHost}:{SmtpPort}`. At dispatch the runner reads the templates from its own signed manifest, asks the Core (`POST /internal/runs/{id}/slot-settings`, resolution-token authorized, audited) for exactly the referenced keys of every binding of that provider, and expands them (manifest defaults fill gaps). Templates over Secret-kind settings are refused on both sides, and a value must be one name, address or port — a setting can never widen an entry into a wildcard or list. The run thus reaches exactly the servers its bindings name, without any vendor host list in platform code.

### Policy resolution

The effective network policy for a run is resolved from three independent layers at dispatch time — the signing record is not involved:

```mermaid
flowchart TD
    MANIFEST["Workflow Manifest (signed)\nbaseline endpoint declarations"]
    RUNCONFIG["Run Configuration\n(trigger / user / AI per-run settings)"]
    PLATFORM["Platform Policy (administrator)\nhard ceiling e.g. block allow-all globally"]

    MANIFEST --> MERGE["Policy Resolver\nmerge manifest + run config, clamp by platform policy"]
    RUNCONFIG --> MERGE
    PLATFORM --> MERGE

    MERGE --> EFFECTIVE["Effective Network Policy for this run\nrecorded in run audit log"]
    EFFECTIVE --> MODE{Effective mode}
    MODE -->|default-deny| ALLOWLIST["Only resolved endpoints reachable"]
    MODE -->|allow-all| ALLOWALL["All endpoints reachable\nstill fully logged"]

    ALLOWLIST --> CAT{Call category}
    CAT -->|Structured API call| PROXY["Resource Proxy\n(credentials managed by platform)"]
    CAT -->|Build toolchain call| EGRESS["Network Egress Layer\n(transparent to the tool)"]
    CAT -->|Undeclared endpoint| BLOCK["Blocked and logged"]

    EGRESS --> PKG_CHECK{Package proxy enabled?}
    PKG_CHECK -->|Yes| PKG_PROXY["Package Proxy\n(cache + scan)"]
    PKG_CHECK -->|No| DIRECT["Direct to declared endpoint"]
    PKG_PROXY --> REG["Public Registry"]
    DIRECT --> REG

    ALLOWALL --> LOG["Full traffic log"]
    BLOCK --> AUDIT["Audit log - flagged"]
```

**Layer responsibilities:**

| Layer | Set by | Scope | Examples |
|---|---|---|---|
| **Workflow manifest** | Workflow author (signed) | Per workflow artifact | Minimum required endpoints, `no-cache` repo flags, `package-proxy` preference |
| **Run configuration** | Trigger, user, or AI at dispatch | Per run | Additional allowed endpoints for this invocation, `allow-all` for a sandbox run |
| **Platform policy** | Administrator | Global or per-tenant | Maximum permitted mode (e.g. deny `allow-all` entirely), mandatory endpoint blocklists |

The effective policy is `merge(manifest_baseline, run_config)` clamped by `platform_policy`. It is computed at dispatch and written to the run audit log — not stored in the signed artifact.

**Manifest declaration (baseline, signed with the workflow):**
```yaml
network:
  allow:
    - endpoint: api.nuget.org
      purpose: NuGet package restore
    - endpoint: registry.npmjs.org
      purpose: npm install
  package-proxy: true
```

**Run configuration (per-run, set at trigger time, not signed):**
```yaml
network:
  mode: allow-all           # operator opt-in for this run only
  allow:
    - endpoint: internal-api.corp.local
      purpose: integration test target
```

**allow-all mode:**
- Requested in the run configuration (per-run) or in platform configuration (global default for a tenant)
- Not part of the signed workflow artifact — the same binary can run in `default-deny` in production and `allow-all` in a sandbox without re-signing
- All traffic is still fully logged — the difference from `default-deny` is that nothing is blocked, not that nothing is observed
- Platform policy is the hard ceiling: administrators can prohibit `allow-all` entirely, regardless of what any run configuration requests
- The effective mode and the identity that requested it are always recorded in the run audit log

### Run pods (companion containers)

A workflow may declare **companion containers** (`RequiresCompanion` — digest-pinned inert
services like a database, broker, or simulated machine; full design in
`docs/test-fabric-and-swarm-design.md`). The runner materializes them on a **private per-run
`--internal` network** before the workflow starts: start order is a declared DAG gated on
readiness, instance counts come from run inputs clamped to the signed bounds, per-run
secrets are minted, and the resolved topology is announced via `Workflow__Companion__*`.
Companions have **zero egress**; the workflow container joins the pod network additionally
(alias `workflow`) and remains the pod's only governed path outside (this section's policy
applies to it unchanged). Declared pod volumes are shared run-scoped scratch space
(`/workspace/pod/<name>` in the workflow container). The pod dies with the run on every
terminal path — state message, container crash, re-adoption, orphan sweep — and each
companion's log tail is persisted as a `companion-log-<name>` artifact. The topology is part
of the signed schema; the registry refuses structurally invalid ones (unpinned images,
cycles) and serves the aggregate spawn summary on the schema DTO for the approval review.

Beyond declared templates, a workflow may declare a **runtime pod-control envelope**
(`RequiresPodControl(max)`): the test itself then spawns/stops companions mid-run via
`IPodController` — runner-mediated over an authenticated queue (never a Docker socket),
audited per operation, clamped to the signed cap on the live **runtime-spawned** count
(declared templates are separately signed topology and never consume the envelope — total
= templates + envelope, exactly what the spawn summary advertises), and restricted to
**configuration-pinned base images**: environment bases may carry a digest-pinned
`ImageReference`, the run's configuration selects its subset (context key `pod-bases`), and
the Core snapshots exactly that map into the dispatch command — default-deny without a
selection, immune to catalog edits mid-run. Software for spawned machines rides the pod
volumes from the workflow's own signed image; there is no exec channel into companions.

---

## 11. Multi-Account Model

A user or service principal holds multiple **Account Bundles**, each scoped to a purpose.

```mermaid
graph LR
    USER["User / Service Principal"]
    USER --> ID["Primary Identity (AAD / LDAP / Local)"]
    USER --> SC_ACC["Source Control Bundle (Git PAT, TFVC credentials)"]
    USER --> TASK_ACC["Task Source Bundle (Jira OAuth, ADO PAT)"]
    USER --> AI_ACC["AI Bundle (Copilot, Azure OAI)"]
    USER --> RES_ACC["Resource Bundle (API keys, DB connections)"]
    ACCT_MGR["Account Manager"] -->|Resolves by purpose at runtime| USER
    WF["Workflow"] -->|Declares required bundle types| ACCT_MGR
```

- Workflows declare *which bundle types* they need - never specific credentials
- Bundles are encrypted at rest **in the Core** (as connectors); Core.Runner decrypts and re-encrypts the relevant scoped subset for a specific workflow instance only at slot activation time — never earlier, and never more than the activated slot requires
- AI bundles carry usage quotas and purpose restrictions
- Multiple bundles of the same type are supported (e.g. two ADO instances, a GitHub and a Jira account)

---

## 12. Deployment Modes

All modes share the same codebase. Runtime behaviour is driven entirely by configuration and pluggable interface implementations.

**Deployment topology.** A deployment runs the two Core services (each with its own database), the admin console UI, and — when triggers/intake should be always-on — a workflow-domain host:

| Service | Role | Database | Scaling |
|---|---|---|---|
| `Auxilia.Core.Api` | Control plane (auth authority, connectors, Run API, Core MCP, live-view + artifact SSE, failover monitor, audit-read, provider catalog, identity import) | Core.Api DB | Stateless behind a load balancer |
| `Auxilia.Core.Runner` | Execution plane (launch, JIT creds, lifecycle, heartbeat-emit) — needs the Docker socket / a container runtime | Core.Runner DB | Competing consumers on the run queue |
| `Auxilia.AdminConsole` | Operator/admin UI (Blazor Server) — a pure `Auxilia.Core.Client` consumer (REST + SSE); holds no database | — (pure Core client) | Sticky sessions (Blazor circuit); no view backplane |
| `Auxilia.TriggerHost` (optional) | Reference host for the `Auxilia.Workflows.Client` engines + email intake — dispatches via the Core Run API, consumes the filtered artifact SSE; any app embedding the library serves the same role | Host-owned storage | Pure Core client; N hosts each own their own triggers |

Platform services (RabbitMQ, MongoDB) are deployed by Compose / k8s as they are today — the Core runs **workload containers only**, it is not a platform supervisor. There is **no Redis/SignalR view backplane**: live views fan out over the Core.Api SSE stream (see §14–§15).

**Images and the reference compose file (2026-11-08).** Every deployable (Core API, runner, Docker guard, egress gateway, admin console, trigger host) and every shipped workflow is published as a versioned image to the GitHub Container Registry (`ghcr.io/<owner>/auxilia-<name>:<version>`, `.github/workflows/publish-images.yml`): built on the same `v*` tags that publish the NuGet packages — one version across images and client libraries —, carrying build provenance and an SBOM, with every image's digest listed per release (workflow types are approved by digest). Images of a private repository are private packages that inherit its access. amd64 only for now. Published images are product images: test-only parts — the runner's credential-probe slot — live in a `system-test` Dockerfile stage that only the system tests build. `compose.yaml` at the repository root runs the platform from those images, or builds them from the source tree: RabbitMQ, the Core API, the Docker guard and the runner (the egress-gateway image is pulled or built with them; the shipped workflow images with the `workflows` profile, then registered by digest with `Scripts/Setup-Platform.ps1 -Registry … -Pull`). It encodes the production shape the system tests prove — the guard alone holds the daemon socket, run directories sit at the daemon's own path for them, the broker account the runner mints per-run identities with, the platform endpoints no run may reach — and refuses to start without a protection key. It runs in the evaluation posture (no trusted signing keys, so no sandbox runtime is required); the production switches (signing keys + a sandbox runtime, MongoDB, a write-once audit anchor) are named in the file. The admin console is not part of it: its browser sign-in needs OIDC behind a gateway that serves it and the Core from one origin.

| Mode | Message Bus | Workflow Runtime | Signing | Identity |
|---|---|---|---|---|
| **Local / Dev** | RabbitMQ in Docker or in-process | OS Process (fully debuggable) — signed `*.workflow.zip` extracted and bind-mounted | Disabled (no ceremony) | Local accounts |
| **On-premise** | RabbitMQ cluster | Docker / k3s — signed `*.workflow.zip` extracted and bind-mounted | HashiCorp Vault Transit | LDAP / AD / OIDC |
| **Cloud (Azure)** | Azure Service Bus | AKS — signed `*.workflow.zip` extracted and bind-mounted | Azure Key Vault | Entra ID |

Pluggable interfaces: **IMessageBusClient**, **IWorkflowRunner**, **ISigningProvider**, **IIdentityProvider**, **IResourceProxy**, **IArtifactStore**
The core platform has no hard dependency on RabbitMQ, Docker, Vault, AAD, or a specific artifact storage backend.

**Isolation guarantees are container-only.** The workspace isolation (mount namespaces, UID isolation, CoW snapshots — section 9) and kernel-level network policy enforcement (section 10) exist only in the container runtimes (Docker / k3s / AKS). In **Local / Dev** mode the workflow runs as a plain OS process — typically on the developer's own machine, including Windows — with **no isolation and no network enforcement**. Dev mode is therefore **trusted-operator-only**: it must never be exposed to untrusted workflows, untrusted users, or production credentials. The security guarantees this document describes apply to the container-based modes.

Endpoint-granular egress enforcement is realized by the per-run egress gateway (§10) on Linux container daemons; a runner on a Windows container daemon cannot enforce egress and runs nothing unless its operator accepts that explicitly (`WorkflowLauncher:AllowUnenforcedEgress`, then allow-all runs only; anything else fails pre-flight). Container hardening (§7 *Workload containment*) applies on every Linux daemon; the second kernel boundary is a sandboxed runtime chosen automatically when the daemon offers one (`PreferredSandboxRuntimes`), configurable (`ContainerRuntime`) and required in the production posture (`RequireSandboxRuntime`).

---

## 13. Open Questions and Concerns

### Resolved from use case analysis (docs/USE-CASES.md)

| # | Decision |
|---|---|
| 1 | WorkflowContext carries: originating work item, named artifact inputs from prior runs, scoped credentials, and optional multi-repo references |
| 2 | ITaskSourceAdapter extended with GetWorkItemDetail (full graph), CreateWorkItem, CreateSubTask, and AttachArtifact |
| 3 | ISourceControlAdapter extended with ListDirectory, GetFileContent, DetectFrameworks for lightweight introspection |
| 4 | Resource Proxy supports both synchronous and async long-running call patterns (trigger + poll/callback) |
| 5 | WorkflowContext supports single or multi-repository references declared in the workflow manifest |
| 6 | Workspace Manager added: warm cache per repo, CoW snapshots per run, mount namespace isolation, multi-source repo support, no-cache flag; write-back happens mid-run via capability-limited source-control slots |
| 7 | Network Egress Layer added: layered policy resolution (manifest baseline + run config + platform ceiling), default-deny, allow-all opt-in, build tool transparency, full audit |
| 8 | Package Proxy added: optional platform mirror for package registries, caching, security scanning, air-gap support |

### Still open

| # | Question | Impact |
|---|---|---|
| 7 | **Pre-flight retry granularity** - Is retry-on-availability configured globally, per workflow type, or per work item? | UX, reliability |
| 8 | **TFVC version scope** - TFS 2015/2017 in scope or only TFS 2019+ / Azure DevOps Server? | Adapter effort |
| 9 | **Work Item Index (deferred)** - Optional similarity search service for refinement quality. Not required for v1. | Future use case quality |

### Intentional design decisions

| # | Decision |
|---|---|
| 10 | **Network policy is layered, not signing-time** — the manifest declares a baseline (minimum required endpoints); run configuration can extend it (e.g. `allow-all` for a sandbox run); platform policy is the hard ceiling (can prohibit `allow-all` entirely). Effective policy is resolved at dispatch and recorded in the run audit log — the signed artifact is unchanged |
| 11 | **AI has full parity with the human UI** - MCP tools are generated from the same API layer, not maintained separately |
| 12 | **Signing is mandatory with no bypass in non-dev modes** - dev mode can disable signing entirely |
| 13 | IMessageBusClient, IWorkflowRunner, ISigningProvider, IIdentityProvider, and IResourceProxy are all pluggable - no hard dependency on any specific technology |
| 14 | **Workflows are trusted via signature, credentials are delivered just-in-time** — the signing authority vouches for the workflow's correctness; a verified workflow may hold scoped credentials directly, but receives each slot's credentials only at slot activation, encrypted per instance, never as an upfront bundle |
| 15 | **Artifact persistence via pluggable `IArtifactStore`** — production is direct but manifest/config-limited; consumption is resolved at dispatch and mounted read-only into the container; the bus carries references (ID + hash), never payloads; backends (filesystem, blob, database) are a deployment choice |
| 16 | **No separate Orchestration Service** — trigger decisions live in the Product; the dispatch authority lives in **Core.Api** (the Run API); container launch, pre-flight, and lifecycle live in **Core.Runner**. A standalone orchestrator would only forward decisions and add a failure mode without an isolation benefit |
| 17 | **Unified principal model with deny-by-default RBAC** — humans, AI agents, and services are all principals through the same Policy Engine (hosted in Core.Api); four built-in roles (Administrator, Operator, User, Auditor) plus **first-class groups** (a principal's effective roles are the union of its direct assignments and its groups'); workflow-type access lists as the primary instrument; single-tenant v1 with `TenantId` on every resource (see section 16) |

---

## 14. Scaling

The admin console UI, the control plane (Core.Api), and the execution layer (Core.Runner) have different scaling characteristics and are solved independently.

---

### 14.1 Admin Console (Blazor Server) Scaling

The Admin Console (`Auxilia.AdminConsole`) is a Blazor Server app, so each browser holds a live circuit pinned to one console instance:

- **Sticky sessions** at the load balancer — a client always returns to the same console instance for the lifetime of its circuit
- **Live views over the Core.Api SSE stream, not a backplane** — when a circuit opens a run's live view the console opens a `GET /api/runs/{id}/stream` SSE subscription to Core.Api; Core.Api's `RunStreamPublisher` consumes the bus status/view topic exchanges **selectively** (see below) and its `RunStreamBroker` fans each event to every open SSE subscription for that run. Each console instance subscribes independently, so **no Redis/SignalR backplane is needed between console instances** — the fan-out point is Core.Api, not the Blazor tier

**Selective event routing (topic exchanges).** The live event feeds are **topic** exchanges with the routing key stamped at publish: `workflow.status` (key `{instanceId}`, extended to `{instanceId}.{commandId}` on the claim transition), `workflow.views` (key = instance id, stamped by the workflow SDK — a change that requires rebuilding every workflow image), and `workflow.artifacts` (key = sanitized artifact type). A Core.Api node binds **only the keys it has an audience for**: the SSE brokers report every subscribe/unsubscribe to their bus publishers (`IRunStreamBindingListener` / `IArtifactStreamBindingListener`), which add/remove queue bindings (`ITopicSubscription.AddBinding/RemoveBinding`) — awaited on subscribe, so the binding exists before the SSE response is flushed. A run-stream audience that only knows the dispatch **command id** gains the instance id as an **alias binding** once the pairing is known (from the claim event, or resolved from the tracked-runs store for late subscribers). The persisting mirrors (`RunTrackingService`, `RunViewTrackingService`, `ArtifactTrackingService`) bind `#` on shared queues — every event, processed once fleet-wide. Per-node ingest therefore scales with the node's audience, not with global event volume. (On Azure Service Bus the same model maps to topic subscription rules.)

Console instances are stateless from a data perspective (pure Core clients). Instances can be added or removed at any time.

```mermaid
graph LR
    subgraph Browsers
        B1[Browser A]
        B2[Browser B]
        B3[Browser C]
    end
    LB[Load Balancer sticky sessions]
    subgraph ConsoleInstances[AdminConsole instances]
        BS1[Console Instance 1 owns circuit A]
        BS2[Console Instance 2 owns circuits B and C]
    end
    API[Core.Api RunStreamBroker]
    BUS[Message Bus status + view topics]
    B1 -->|Sticky| LB
    B2 -->|Sticky| LB
    B3 -->|Sticky| LB
    LB --> BS1
    LB --> BS2
    BUS -->|status + views, only keys with an audience| API
    API -->|SSE stream per viewed run| BS1
    API -->|SSE stream per viewed run| BS2
    BS1 -->|Only to circuit A| B1
    BS2 -->|Only to circuits B and C| B2
    BS2 --> B3
```

---

### 14.2 Core.Runner Scaling

Core.Runner instances scale via **competing consumers** on the RabbitMQ run-request queue — adding instances increases dispatch throughput automatically.

Once a Core.Runner picks up a run it becomes the **owner** for its lifetime (it holds the container/process handle). Two concerns follow:

- **Ownership tracking** — each run's lifecycle record carries the owning runner's service ID (also propagated on `WorkflowStatusEvent.OwnerServiceId` / `CommandId`); each runner writes liveness heartbeats. The runner keeps both in its own **platform data layer** (`ServiceHeartbeatRecord`) and additionally publishes a `RunnerHeartbeat` on the bus. There is **no Redis** in this path; moving the hot heartbeat path onto a cache is a possible later optimization, not a semantic change
- **Failover** — each Core.Runner publishes a `RunnerHeartbeat` (`platform.runner-heartbeats`); the **failover monitor now runs in Core.Api** (`FailoverMonitor` + `RunnerLivenessTracker`), tracking liveness purely from the bus beat so it never reads the runner's database (the Core/Runner DB split holds). When a beat goes stale it finds the dead runner's non-terminal runs in the **Core's own** store, re-reads each fresh and takes the `Failed` transition as a versioned compare-and-swap (a run that completed meanwhile is untouched; with N Core.Api nodes only the winning node acts), terminates it gracefully via its per-run cancel queue (no Docker access required), and re-dispatches the stored dispatch command **once** (guarded so a re-failed run is not re-dispatched again) **as the original triggering principal** from the resolution stash — so the connector and catalog gates re-check against them and a run over a personal connector fails over exactly as it was first authorized. Recovery is always a fresh restart from scratch (workflows are not resumable, see section 6) — exactly once per orphaned run, so failovers never cascade; the failover event is surfaced in the UI so the user sees the run was restarted. Three companion sweeps/guards complete the picture: the **claim-timeout sweep** fails `Dispatched` runs no runner ever claimed (`dispatch-never-claimed`; skipped under `AllowDispatchWithoutRunner`, which deliberately queues), the **zombie sweep** fails stale non-terminal runs whose owner was never heard from (Core restart lost the liveness memory), and the run-tracking **terminal sink** guarantees a terminal record is never resurrected by late events from stale commands or re-adopted runners — enforced with optimistic concurrency (`CoreRunRecord.Version` + conditional saves), because the shared run-tracking queue round-robins a run's transitions across Core.Api nodes and the claim-timeout sweep can race a claim; every state write is a read-check-CAS, so the losing writer re-reads and re-applies the rules instead of overwriting. A short-downtime runner restart usually beats the clock instead: the runner **re-claims** its still-running containers under its fresh ServiceId at startup (see §Core.Runner), so the runs continue without any failover

```mermaid
graph TB
    subgraph MessageBus[Message Bus RabbitMQ]
        CMD[Command Queue competing consumers]
        HB[platform.runner-heartbeats]
        EVT[workflow.status topic]
    end
    subgraph RunnerPool[Core.Runner Pool]
        SI1[Core.Runner 1 owns WF-101 WF-102]
        SI2[Core.Runner 2 owns WF-103]
        SI3[Core.Runner 3 idle]
    end
    subgraph Workflows[Workflow Containers / Processes]
        WF101[Workflow WF-101]
        WF102[Workflow WF-102]
        WF103[Workflow WF-103]
    end
    MON[Core.Api FailoverMonitor + RunnerLivenessTracker]
    TRIG[Trigger sources: Product adapters, Core Run API / MCP, Studio scheduler]
    TRIG -->|Dispatch command| CMD
    CMD -->|Competing consume| SI1
    CMD -->|Competing consume| SI2
    CMD -->|Competing consume| SI3
    SI1 <--> WF101
    SI1 <--> WF102
    SI2 <--> WF103
    SI1 -->|RunnerHeartbeat| HB
    SI2 -->|RunnerHeartbeat| HB
    SI3 -->|RunnerHeartbeat| HB
    HB -->|liveness| MON
    SI1 -->|Status events with owner + command id| EVT
    SI2 -->|Status events| EVT
    EVT --> MON
    MON -->|Re-dispatch orphan once per retry policy| CMD
```

---

### 14.3 Full Scaled Event Flow

End-to-end path of a workflow status update from container to browser when fully scaled out:

```mermaid
sequenceDiagram
    participant WF as Workflow Container
    participant BUS as RabbitMQ
    participant SI as Core.Runner owner
    participant API as Core.Api RunStreamBroker
    participant BS1 as Console Instance 1
    participant BS2 as Console Instance 2
    participant UA as Browser A subscribed
    WF->>BUS: StepCompleted message
    BUS->>SI: Delivered to owning Core.Runner
    SI->>BUS: Publish WorkflowStatusEvent (+ ViewData)
    BUS->>API: status / view topics, delivered only to nodes bound to this run
    API->>BS1: SSE frame on this run's open stream
    Note over BS2: No open stream for this run - nothing sent
    BS1->>UA: Render on subscribed circuit
```

---

### 14.4 Infrastructure Summary per Mode

| Component | Local / Dev | On-premise | Cloud (Azure) |
|---|---|---|---|
| Admin Console instances | 1 | N + sticky LB (live views via Core.Api SSE) | N + Azure Front Door (live views via Core.Api SSE) |
| Core.Runner instances | 1 | Pool via competing consumers | Pool via competing consumers with auto-scale |
| Ownership and heartbeat store | Not needed | Runner platform data layer (MongoDB) + `RunnerHeartbeat` on the bus | Runner platform data layer + `RunnerHeartbeat` on the bus |
| Message bus | RabbitMQ single in Docker | RabbitMQ cluster | Azure Service Bus |
| Workflow runtime | OS Process | Docker / k3s | AKS node pool |

Live-view fan-out uses the **Core.Api SSE stream**, not a Blazor/SignalR backplane, so **no Redis is required for the UI tier**. Runner ownership and liveness live in the runner's platform data layer plus the `RunnerHeartbeat` bus beat — also no Redis. (Moving the hot heartbeat path onto a cache such as Redis remains an optional later optimization.)

---

## 15. Live View Data and Pluggable Dashboards

Workflows expose data to the frontend through a single general mechanism: **named, schema-declared views**. A live agent conversation, a progress log, a findings table, and a finished run's result page are all the same concept — only the rendering hint and the data lifecycle differ.

### View declaration

The workflow schema (embedded in the signed package) declares the views a workflow provides:

```yaml
views:
  - name: agent-conversation
    schema: AgentMessage          # JSON schema of one data item
    rendering: stream             # hint: stream | log | table | chart | markdown | custom
    lifecycle: live+persisted     # live, persisted, or both
  - name: review-findings
    schema: CodeReviewFinding
    rendering: table
    lifecycle: persisted
```

### Data flow

```mermaid
graph LR
    WF["Workflow"] -->|"ViewData message<br/>(instanceId, viewName, sequence, payload)"| BUS["Message Bus (IMessageBusClient)"]
    BUS -->|"workflow.views topic, key = instance id"| API["Core.Api: RunStreamPublisher → RunStreamBroker"]
    API -->|"SSE: GET /api/runs/{id}/stream"| FE["Auxilia.AdminConsole: renders view from descriptor"]
    BUS -->|"lifecycle includes persisted"| STORE["View Store (Core.Runner / Product DB / IArtifactStore)"]
    STORE --> FE2["Auxilia.AdminConsole: re-opens views of finished runs"]
    API --> MCP["MCP — same view data exposed to AI agents"]
```

- Workflows publish `ViewData` messages: an envelope of `(instanceId, viewName, sequence, payload)` where the payload conforms to the declared view schema and `sequence` counts the run's items **across all its views** (1, 2, 3, … — assigned and published under one gate, so it is the publish order and a missing item is visible). The bus carries only view data items — large blobs belong in the Artifact Store, referenced from the payload
- **Live**: Core.Api's `RunStreamPublisher` consumes the `workflow.views` topic exchange (bound only to runs with an open stream, section 14.1) and its `RunStreamBroker` re-emits each item over the SSE stream (`GET /api/runs/{id}/stream`, section 14); the Admin Console consumes that SSE on its Blazor circuit and renders from the descriptor — a live agent view is simply a view with `rendering: stream`. There is **no SignalR/Redis backplane**
- **A resumable stream**: every frame's SSE `id` is the stream's position after it (`RunStreamEvent.Cursor` — the last view sequence plus a mark of the last status); a client that sends it back (`Last-Event-ID`, which a browser's `EventSource` does on every reconnect, or `?after=` on a first connect — the header wins) gets everything after it **exactly once, in order**, and a client connecting without one gets the run from its start. `RunStreamSession` subscribes BEFORE it reads the record and the store, replays the stored items after the position, then follows the bus: a duplicate is dropped by sequence, an item ahead of its predecessors is held back while the store is re-read for them, and predecessors the Core never retained (beyond `MaxPersistedViewItemsPerRun`) become one `gap` frame after `CoreApi:StreamGapWaitMilliseconds`. **Status frames carry the run's state as the record holds it** — a bus transition only prompts a re-read of the record (another node may still be writing it), so a client never sees a state the Core does not hold and never goes back to an older one on a resume; transitions closer together than that, or while no stream was open, collapse into the latest. A live run's state is the first frame, a finished run's verdict the last: before it the stream waits (bounded) until it has every item up to the count the run reported with its final state (`LastViewSequence`), the rest becomes a gap. A position at the end of a finished run is answered **204** — nothing more, ever, which also stops an `EventSource`. Idle streams carry **`: ping` keepalive comments** (`CoreApi:SseKeepaliveSeconds`, default 15s) so a dead connection is distinguishable from a quiet run. The client side lives INSIDE `Auxilia.Core.Client`: streams reconnect with exponential backoff and the last yielded frame's cursor, enforce an idle timeout, take a kept position to start from, and surface `Connected`/`Reconnecting` frames so UIs show a banner instead of freezing. `GET /api/runs/{id}/views?after=` pages the stored items in the same order. Artifact-stream consumers catch up on a reconnect via `GET /api/artifacts?createdAfterUtc=…` (oldest-first) — artifacts are persisted, so nothing is lost in the gap
- **Persisted**: view data is stored so the Admin Console can re-open the views of completed runs — viewing finished workflow results uses the identical rendering path as live data, replayed from the store
- **Generic rendering**: the frontend renders views purely from the descriptor (schema + rendering hint); adding a new workflow with new views requires **no frontend changes**. A `custom` rendering hint allows future pluggable visual components
- **AI parity**: the MCP server exposes the same views to AI agents — no hidden data channel

### Dashboard composition

Dashboards are composed of views: per-run dashboards come from the run's workflow schema automatically; operators can pin views from multiple workflows into shared dashboards. Access to a view follows the same permission model as the workflow it belongs to.

---

## 16. Governance and RBAC

Every operation in Auxilia — human, AI, or service — is performed by an authenticated **principal** and authorized by the **Policy Engine**, hosted in **Core.Api** as the platform's single authentication + authorization authority. There are no anonymous operations and no unaudited decisions. See `docs/delivered/governance-rbac-design.md` for the implementation plan.

### Principals

| Principal kind | Authenticates via | Notes |
|---|---|---|
| **Human user** | Identity Provider (Local, OIDC/AAD/LDAP) | Interactive dashboard sessions |
| **Service principal** | Platform-issued API key (shown once at creation, stored hashed) | Integration adapters, schedulers, AI agents — every non-human identity |

Both kinds flow through the same role, permission, and audit machinery. Finer classification is
**tags**: free-form, admin-managed strings on the principal (e.g. `ai-agent`, a team, an
environment) — a filter/grouping/reporting axis, never a policy input. An AI agent is therefore
not a special principal kind; it is a Service principal tagged as one.

### Built-in roles

| Role | May |
|---|---|
| **Administrator** | Configure identity providers and group mappings, platform policy ceilings, tenants, retention, signing configuration; assign roles; manage all principals |
| **Operator** | Install/enable workflow types, manage slot configurations and operator clamps (branch patterns, PR-only, network endpoints), approve long-living workflow deployment, configure task sources and triggers, manage shared dashboards |
| **User** | Trigger permitted workflow types, observe permitted runs and views, answer RequestInput / approval steps routed to them, use per-run dashboards, fetch their runs' artifacts (checkpoints included — run access keeps them to the caller's own runs) |
| **Auditor** | Read-only access to the audit log and run history — no operational permissions |

Roles are **permission sets**, not flags: v1 ships these four fixed roles; custom roles are a schema-compatible later step. A principal can hold multiple roles.

### Permission model

An authorization check is the triple **(principal, action, resource)** evaluated **deny-by-default**:

- **Actions** are fine-grained verbs per resource type: `workflow.trigger`, `workflow.cancel`, `run.observe`, `run.provide-input`, `view.subscribe`, `artifact.consume`, `slot-config.write`, `bundle.manage`, `policy.administer`, `audit.read`, …
- **Resources** carry a scope chain: `tenant → workflow-type → run/view/artifact`. A grant at an outer scope implies the inner ones unless explicitly narrowed
- **Workflow-type access lists** are the primary day-to-day instrument: per workflow type, entries grant an action to a role, an individual principal, or every member of a first-class group; when entries exist for a (type, action) they are the exclusive grant source (administered via MCP and REST, gated `policy.administer`). With no entries, the `security.default-resource-access` posture decides who may trigger (restricted = administrators only; see "Resource ownership and sharing")
- v1 is **single-tenant**, but every resource and grant carries a `TenantId` (default tenant) so multi-tenancy is a data migration, not a schema break

### Policy Engine evaluation

```mermaid
flowchart LR
    REQ["Request (UI / MCP / dispatch)"] --> AUTHN["Authenticate via IIdentityProvider"]
    AUTHN --> RESOLVE["Resolve principal + roles<br/>(direct + group membership + IdP mappings)"]
    RESOLVE --> RBAC["RBAC check: (principal, action, resource)<br/>deny by default"]
    RBAC -->|denied| AUDIT_D["Audit: deny"] --> BLOCK["Blocked"]
    RBAC -->|allowed| RESPOL["Resource policy checks:<br/>approval requirements, AI purpose restrictions,<br/>quotas, operator clamps"]
    RESPOL -->|violated| AUDIT_D
    RESPOL -->|passed| AUDIT_A["Audit: allow"] --> EXEC["Operation proceeds"]
```

Both outcomes are always audited. The Policy Engine is a library used at every enforcement point in the Core — every REST endpoint, the Core MCP, dispatch (the Run API's trigger check), and administration operations — and the Product authorizes through the Core, so UI and MCP cannot diverge.

### Resource ownership and sharing

Connectors and run configurations share one ownership model (`ResourceScope` + `AccessGrant` in the contracts, evaluated by the Core's `AccessGrantEvaluator`):

- **Personal** (the default on creation) — owned by the creating principal; visible and usable only to the owner, granted subjects, and managers. Grants admit a **principal**, every member of a **first-class group**, or an **AD directory group** (matched against the groups captured at the principal's last federated sign-in). Invisible resources read as *not found*, and what a principal cannot see it cannot run or bind.
- **Company** — shared platform-wide; creating one is the deliberate opt-in gated by the resource's management permission (`slot-config.write` for connectors, `workflow-configuration.manage` for configurations).
- Editing, deleting, and re-sharing a personal resource is **owner-or-manager**; company resources are manager-only.
- Clients gate what they display on `CurrentPrincipal.Permissions` (the role-derived effective action set from `GET /auth/me`); grant editors pick subjects from `GET /api/directory/subjects`, which exposes ids and display names only — sharing needs a people picker, not principal administration.
- **Platform resources restrict rather than own.** Workflow TYPES are governed by the Policy Engine's per-(type, action) access list (`WorkflowTypeAccessStore`): the FIRST entry makes the list the EXCLUSIVE grant source for that (type, action) — enforced at every dispatch. Administered via `policy.administer` over MCP and REST (`/api/workflow-types/{type}/access`). Catalog-curated bindables — slot PROVIDERS and environment LAYERS alike — share ONE optional `AccessGrant` list stored on the provider-catalog curation record (`PUT /api/provider-catalog/{type}/grants`, with `PUT /api/environment-layers/{type}/grants` as the layer-flavored twin): non-empty admits only the listed subjects, enforced by a single dispatch gate; upserts never widen grants.
- **Default-deny resource access (2026-08-08).** A grantable platform resource with NO explicit grants follows the platform posture `security.default-resource-access` (a runtime platform setting): **restricted** — the default, including when unset — means an ungranted workflow type may be TRIGGERED only by administrators and an ungranted provider/layer may be BOUND only by administrators (a provider the catalog does not know at all — installed on a runner, never registered — counts as ungranted: naming its type inline never bypasses the posture); **open** restores role-governed access (the pre-hardening behavior). Only trigger/bind — resource USE — is posture-gated: other type-scoped actions (cancel, observe, approve) stay role-governed, so granting trigger never forces enumerating every action. **Administrators always pass** both empty and non-empty grant lists — they administer the lists, so excluding them is bootstrap pain, not protection. System dispatches without a principal (the approval pipeline) are exempt: the Core itself is the actor; a failover re-dispatch acts as the original triggering principal and is gated like them. The runner's pre-flight re-check stays a role-permission defense and never re-applies the restricted default (the Core owns the posture and already gated the dispatch).

### Run access (2026-11-08)

A run carries its content — agent conversations, repository excerpts, terminal sessions, the
container and egress logs — so it is authorized **per run**, not by role alone (`RunAccessService`):

- **Who reaches a run.** The principal it **runs for** (the triggering principal; on a delegated
  start — `RunRequest.RequestedBy` / `onBehalfOf`, gated `run.on-behalf-of` — the one it was started
  for), the principal that **started it** on that one's behalf, the subjects its **grants** admit
  (the connector sharing model: principals, first-class groups, directory groups), and
  **administrators** — whose reaching someone else's run through a content-bearing or steering
  surface (stream, views, inputs, terminal, cancel, rerun, grants, artifact payloads) is audited
  (`run.admin-access`). A platform dispatch without a principal (the approval pipeline) is
  reachable by administrators only.
- **Where.** Every run surface: the status read and the list/stats (scoped to the caller's runs),
  the SSE stream (authorized after subscribing and before a single frame, so the
  subscribe-before-snapshot ordering holds), stored views, inputs, the terminal ticket, cancel,
  rerun and dashboard pins; the run's **artifacts** (listing, metadata, payload, the filtered
  artifact SSE) and the **events** it published (query and SSE — events without a source run
  stay role-gated); and the same tools over MCP. Someone else's run is indistinguishable from a
  missing one — **404** everywhere. Deleting a run's checkpoint (§9) is for the principal it runs
  for, its starter and administrators — a grantee reads it, never deletes it (**403**).
- **Sharing.** `PUT /api/runs/{id}/grants` (MCP `set_run_grants`, `ICoreClient.SetRunGrantsAsync`)
  by the principal it runs for, its starter, or an administrator — a grantee may not re-share; an
  empty list makes the run private again; audited `run.grants-set`. `RunStatus` carries
  `RunsFor`, `StartedBy` and `Grants`.
- **Lifecycle.** The owners are stamped on the `Dispatched` record (there is no moment anyone else
  may reach the run), survive the claim's rekey to the instance id (recovered from the resolution
  stash when no dispatch record is merged), and carry over a failover re-dispatch together with
  the grants — the same logical run. An operator rerun is a new run, for the rerunning principal.
- **Roles still apply.** Run access decides WHICH runs; the role (`run.observe`,
  `run.provide-input`, `run.open-terminal`, `workflow.cancel`, `artifact.consume`,
  `event.consume`) decides WHAT a caller may do with them. Chaining hosts (the trigger host) see
  the artifacts and events of the runs they start; a cross-principal chain needs a grant.

### Groups and group mapping

Roles reach a principal three ways, unioned deny-by-default: **direct** assignment; membership in a **first-class group** (`GroupDirectory` + `GroupRoleResolver` — a group carries members and roles, and a member inherits the group's roles); and IdP **mapping**, where an administrator maintains `(identity provider, group) → role(s)` mappings so corporate directory groups (AAD/LDAP/OIDC claims) translate to Auxilia roles at sign-in without per-user administration. A principal's **effective roles are the union of all three** — and that union is what the session's role claims carry, so `/auth/me`, claims-only permission checks and the visibility filters agree with the Policy Engine. First-class groups and their role assignments are administered in the Core (REST + authenticated MCP: `create_group`, `add_group_member`, `assign_group_role`); because group roles fan out to every member, the Administrator-reaching group mutations are step-up-elevation-gated exactly like a direct Administrator grant (see "Safeguards for principal administration"). IdP mappings are evaluated at session start and cached for the session lifetime.

### Non-browser sign-in (2026-08-03)

Desktop/CLI clients (the steering client) sign a human in without a browser: `POST /auth/login`
(anonymous by design — it IS the sign-in) verifies username + password through the identity
provider and mints the **same per-user bearer** the browser path issues, so per-user audit and
separation of duties apply to desktop clients too. The desktop lifetime
(`CoreSecurity:LoginTokenLifetimeMinutes`, default one workday) is longer than the console
token — there is no cookie session to silently re-mint from, and the client must never hold
the password to renew. Disabled principals cannot sign in; both outcomes are audited
(`auth.login` granted/denied).

Brute-forcing is never free: the endpoint is rate-limited per client IP (fixed window,
`CoreSecurity:LoginRateLimitPermitsPerMinute`, default 5/minute) and, inside the endpoint, a
per-username failed-attempt throttle (`CoreSecurity:LoginFailureLimitPerUsername` within
`LoginFailureWindowMinutes`, defaults 5 in 5) refuses further attempts for that account even
when the attacker rotates IPs — cleared by a successful sign-in. Either refusal is a 429 with
an `auth.login` rate-limited audit entry, and a throttled attempt is refused before the
password is checked. `POST /auth/token` needs no such limit: it mints only from an
already-authenticated cookie session, never from a password.

### Runtime platform settings (2026-08-03)

Security knobs an administrator changes WITHOUT a redeployment live in the platform-settings
store (`GET/PUT /api/platform-settings`, gated `policy.administer`; writes additionally demand
a step-up elevation — they shape the security posture). Known keys only (a typo fails loudly);
an unset key falls back to the deployment configuration, and an empty value clears a key back to
it (audited `platform-settings.cleared`). Keys:
`auth.login-token-lifetime-minutes` — the desktop sign-in session lifetime (deployment default
one week), applied to every NEW sign-in immediately; and `security.default-resource-access`
(`restricted` | `open`, unset = `restricted`) — whether an ungranted grantable resource is
administrators-only or role-governed (see "Resource ownership and sharing"), applied to every
subsequent dispatch immediately; and the run-checkpoint lifetime rules (§9 *Run checkpoints*) —
`artifacts.checkpoint-lifetime-hours` (the general lifetime), `artifacts.checkpoint-lifetime-overrides`
(`run` | `workflow` | `none`, unset = `run`) and `artifacts.checkpoint-lifetime-max-hours` (unset =
the deployment's `CoreApi:CheckpointLifetimeMaxHours`, default 720 h), applied from the next save on. Deliberately NO config-side ceiling: the
deployment default is a reasonable security default, not a babysitter — the admin surface
(audited, elevation-gated) is trusted to override it in either direction. The AdminConsole
surfaces all of them on `/admin/settings` (gated `policy.administer`).

### Safeguards for principal administration

Two server-side safeguards protect the identity plane itself (clients may add type-to-confirm
UX on top, but these are enforced in the Core and apply equally to REST and MCP):

- **Last-administrator lock-out guard** — revoking the Administrator role or disabling a
  principal is refused (HTTP 409) when it would leave **no enabled administrator**. Group-derived
  admin roles count both ways: a direct revoke is allowed while the target keeps admin through a
  group, and another principal's group-held admin role satisfies the guard.
- **Step-up re-authentication** — holding `principal.administer` is not enough for the most
  sensitive mutations: **granting or revoking the Administrator role and disabling a principal**
  additionally demand a fresh **elevation**. The gate covers the **group path to the same
  privilege** — assigning the Administrator role to a group, and adding a member to a group that
  currently holds Administrator (an admin grant to that member) — since group roles are unioned
  into effective roles. Removals (revoking a group role, removing a member) lower privilege and
  need no elevation. The caller re-proves their *own* credential
  (password / API key) at `POST /auth/step-up` and receives a self-validating HMAC ticket
  (principal + expiry, ~5 minutes — the same node-portable construction as terminal tickets),
  sent as the `X-Auxilia-Elevation` header (MCP: the `step_up` tool + an `elevationToken`
  parameter). Without it those calls fail with the error detail `elevation-required`, which
  clients key their re-authentication prompt on. Step-ups are audited, granted and denied.
  Step-up is **throttled exactly like the login** — the per-IP `auth-login` fixed window plus
  the per-account failure throttle (`LoginAttemptThrottle`, keyed by PRINCIPAL for step-up, REST
  and MCP alike; a throttled attempt is refused before the secret is checked and audited
  `rate-limited`) — so a leaked bearer cannot be brute-forced into an elevation.

### Administration

Identity administration (principals, role assignments, **groups**, group mappings, platform policy ceilings, retention, the audit-log viewer) is a **Core.Api** responsibility — surfaced through the Core admin console and the Core MCP. Workflow-domain configuration (slot→connector bindings, operator clamps) is administered against the Core's configuration store today; **trigger definitions are owned by whichever host embeds the `Auxilia.Workflows.Client` engines**. Everything authenticates against the one Core policy authority, so UI and MCP cannot diverge. The admin pages are surfaced by `Auxilia.AdminConsole`, a pure Core client.

---

*Document maintained in docs/ARCHITECTURE.md — update alongside major design decisions. See also docs/USE-CASES.md.*