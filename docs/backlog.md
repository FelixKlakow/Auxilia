# Auxilia — Backlog

> Live tracker of known follow-ups. Captured 2026-07-26 at the close of the BackendService retirement + the steering client first-client rework. Delivered programs live in `docs/delivered/`; this is what's *left*.

## Core.Api — client-surface follow-ups (surfaced during the retirement)
- ~~**Config *update* endpoint**~~ — **DONE 2026-07-27**: `PUT /api/configurations/{id}` + `PUT /api/connectors/{id}` (rename + key-wise settings upsert — the credential-refresh path), `ICoreClient.UpdateConfigurationAsync`/`UpdateConnectorAsync`; the steering client edits configurations (prefilled wizard) and connections (edit-in-place form; empty = keep stored value). AdminConsole editor still "saves as new".
- ~~**`PackageUri` per workflow type**~~ — **DONE 2026-07-26** with the workflow-type registry (ARCHITECTURE §7): types register permanently with their signed package; runs/configs are type-only, the Core resolves the coordinate, and `WorkflowTypeDto`/`WorkflowSchemaDto` carry `PackageUri` + `Status`. Follow-ups: Studio's authoring catalog should *register* its types into the Core instead of keeping its own `PackageUri`; AdminConsole has no registry-administration UI yet (register/approve/deny run via REST/MCP); the AI safety-check approval handler (static workflow over the submitted package) is a designed-but-unbuilt pipeline handler.
- **Trigger-CRUD API** — triggers live in Studio (headless); the AdminConsole editor links out. Decide console↔Studio vs Core-proxied, then build trigger create/edit/enable/disable.
- ~~**Persisted view-read**~~ — **DONE 2026-07-26**: `RunViewTrackingService` mirrors `ViewDataMessage` into Core-owned `CoreRunViewRecord` (capped per run), `GET /api/runs/{id}/views` + `ICoreClient.GetRunViewsAsync` read it back; the steering client backfills finished runs' outputs from it. AdminConsole RunDetail could now use the same read path (not yet wired there).
- **Rerun endpoint** — no `ICoreClient` rerun; re-dispatch from the stored dispatch command + lineage.
- **Dashboard stats/pins** — no stats endpoint (counts computed client-side from a query page) and no persisted pinned dashboards.
- **`RunStatus` completion time / duration** — not carried; Runs shows start time only.
- **Session terminal** — the old ttyd reverse-proxy (`SessionTerminalProxy`) has no Core equivalent; dropped from the console.
- **Built-in role list endpoint** — the AdminConsole hard-codes the role list (no Core list-roles); actor→display-name resolution in Audit ties to a principals lookup.
- **Legacy `ConnectorRecord` cleanup** — superseded by `CoreConnectorRecord`; verify no consumer, then remove.
- **Per-user bearer hardening** — the short-lived bearer is embedded in the prerendered page (same-origin TLS); consider a server-side opaque-handle store keyed by principal.

## Config-store ownership (Felix's model — deferred as its own step)
- **Move the persisted config store out of the Core.** "The Core doesn't own persisted workflows." Today `/api/configurations*` + `RunConfigurationAsync` still live in Core (tangled with the `CoreApiDispatch` acceptance test). Target: the product owns the config store; the Core validates a submitted spec against its **schema registry** and runs it via the Run API.

## Human-steering — the full loop (the steering client)  ·  see `docs/steering-client-integration.md`
- ~~**Deliver-input (P2)**~~ — **DONE 2026-07-26**: `POST /api/runs/{id}/inputs` + `ICoreClient.ProvideInputAsync` (authorizes `run.provide-input`, audits, publishes `WorkflowInputMessage` to the instance's dedicated INPUT queue `workflow-response-{id}-inputs`; accepts dispatch-command or instance id — a standing subscriber on the main response queue would compete with slot-activation/configuration responses, found + fixed 2026-07-27). steering client guidance/approve/reject/halt are live.
- ~~**SDK "await a signal into a live run"**~~ — **DONE 2026-07-26**: `IWorkflowInputs.ReceiveAsync` (channel-fed from the response-queue subscription), registered by the WorkflowBuilder. `echo-decision-workflow` (Workflows.Testing) exercises the full propose→hold→decide→echo loop over the raw wire protocol.
- ~~**The steerable workflow**~~ — **DONE 2026-07-27**: the SDK gained `OperatorChannel` (capabilities, question forms, guidance, halt, session-ended over the steering wire protocol) and the coding-agent session engine (`AgentSessionApplication`, shared by `claude-code` AND `github-copilot`) bridges agent questions to operator forms. The Claude provider runs interactively (`--input-format stream-json`, `can_use_tool` control requests decided by the operator, guidance injected as user turns); verified live end to end with the stub.
- ~~**Per-repo working directory**~~ — **DONE 2026-07-27** (as data): `git-repository` declares a `WorkingDirectory` setting with role `working-directory`; each mount's effective root (clone + working dir) is announced as `Workflow__WorkspaceMount__<ID>`. Open sliver: the coding-agent context still uses `ISourceControlAccess.WorkingPath`/`Workflow__WorkspaceDirectory` for its cwd — consume the per-mount variables there.
- **Connector browse beyond GitHub** — `POST /api/connectors/{id}/browse` (live repo/branch lists, credential stays Core-side) implements GitHub only; TFS/Azure DevOps fall back to manual entry.
- **Zombie-run sweep** — a run whose terminal/failure event is lost (Core.Api or Core.Runner down at the wrong moment) lingers as Running: container-exit watchers do not survive a runner restart, and the FailoverMonitor only fails over runners it has SEEN heartbeat (in-memory). Needs: runner re-adopting (or reaping) its containers on start + a Core-side staleness transition (Running w/o owner heartbeat → Failed). Interim: `DELETE /api/runs?includeStale=true&staleMinutes=N`.
- ~~**§4 credential-model decision**~~ — **DECIDED + BUILT 2026-07-27: Model B + interception**. `AllowPush` on the repository binding keeps the scoped credential on the (always-fresh) per-run clone; `git push` is classified as the PUSH action kind and governed by `push-policy` (ask/auto, independent of the general mode, live-changeable); clones get a provisioned commit identity (`CommitName`/`CommitEmail` binding settings, platform default otherwise). Open slivers: a token *scoped to push* (today it's the connector's token as-is — GitHub fine-grained PATs recommended); real-CLI live push verification (unit/component-verified so far); per-action policies for future kinds (deploy, mail) when they appear.
- **the steering client cross-repo dependency → NuGet** — replace the side-by-side-clone `ProjectReference`s to `Auxilia.Core.Client`/`Contracts` (+ a standalone steering-codec package) with published packages.
- **the steering client desktop per-user sign-in** — replace the API-key principal with interactive (device-code/OIDC) sign-in that mints a per-user bearer (per-user audit/SoD). Needs a Core non-browser token-issue path.

## Environment capabilities (decided + BUILT 2026-07-27)
Felix's model, delivered: capabilities are catalog entries (`ComposesEnvironment`, category
`environment`, runtime-extensible); agent workflows declare an optional multi-binding
`environment` slot; the Core routes selections generically (`EnvironmentCapabilities` on the
command); the RUNNER maps capability → Dockerfile fragment (`WorkflowLauncher__EnvironmentLayers`)
and composes fragments onto the workflow image, content-addressed (`auxilia-env:<hash>`) — each
combination builds once, then cache-hits. `dotnet-10` and `node-22` fragments ship under
`Source/Auxilia.Core.Runner/environment-layers/`; a "template" is a capability whose fragment
installs a whole stack. Verified live (dotnet-10 → 10.0.302 in the composed image). Still open:
Windows-container runners (separate Docker engine mode), composed-image GC, and surfacing
capability descriptions more richly in the steering client gallery.

## Generic binding pipeline — follow-ups (2026-07-27)
- ~~**Docker system-test pass on the mount pipeline**~~ — **PASSED 2026-07-27**: `RepositoryWorkspaceSystemTests` (generic `git-repository` binding, roles + `git-credential`, authenticated git server) green on real Docker. Still open: OS-sensitive `DockerSocketPath` default (Docker Desktop 29.4.2 broke the unix-socket default on Windows hosts — local runners need `WorkflowLauncher__DockerSocketPath=npipe://./pipe/docker_engine`).
- **AdminConsole/Studio editors on the generic model** — the steering client renders bindings from catalog descriptors; the Blazor editors still assume connector-only bindings.
- **Copilot interactivity** — the Copilot CLI's headless mode has no control channel; revisit with the Copilot SDK's server mode (JSON-RPC) for questions/guidance parity with Claude.
- **Real-CLI interactive verification** — the steered permission loop is stub-verified; run once against the real `claude` CLI (`--permission-prompt-tool stdio`) with a real account.
- **OAuth refresh-token capture** — the `anthropic-claude` connect flow snapshots the local CLI's *access* token, which the CLI rotates (the stored copy 401s as "revoked" within hours — bit Felix 2026-07-27). Capture the refresh token too and refresh Core-side at JIT delivery (or push users to API keys for connectors). Interim: Edit → Connect… refreshes the snapshot in place.

## CI validation — Docker system tests (Phase-4 assumptions, not runnable locally)
- Email slot **plugin-dependency loading** (highest risk — MailKit/MimeKit/BouncyCastle copied alongside the provider DLL).
- Core resolves fake code-review slots from inline `ProviderType`+`Settings` bindings.
- Declared-slot set of `pull-request-code-review` matches the six seeded bindings.
- Studio null-protector format (seeded `ProtectedSettingsJson` as plain JSON).
- `/api/audit` response shape (camelCase `PagedResult`); Failover timing (8s timeout / 2s scan; owner stamped on the status event; re-dispatch queue).

## DevStand
- `ScreenshotHarness` stubbed (targeted the retired dashboard) — rewire to boot `Auxilia.AdminConsole` in `EndToEndEnvironment`.

## Deeper platform consolidation (from the separation plan)
- Core.Api + Core.Runner shared **Core DB tier** (currently separate DBs; several features bridge the split over the bus).
- Endpoint-granular network-policy enforcement + **Run-API quotas** (hardening).
