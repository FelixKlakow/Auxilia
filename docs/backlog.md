# Auxilia — Backlog

> Live tracker of known follow-ups. Captured 2026-07-26 at the close of the BackendService retirement + the steering client first-client rework. Delivered programs live in `docs/delivered/`; this is what's *left*.

## Core.Api — client-surface follow-ups (surfaced during the retirement)
- **Config *update* endpoint** — `ICoreClient` is create-only (`CreateConfigurationAsync`); the AdminConsole editor "saves as new". Add update.
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
- ~~**Deliver-input (P2)**~~ — **DONE 2026-07-26**: `POST /api/runs/{id}/inputs` + `ICoreClient.ProvideInputAsync` (authorizes `run.provide-input`, audits, publishes `WorkflowInputMessage` to the instance's response queue; accepts dispatch-command or instance id). steering client guidance/approve/reject/halt are live.
- ~~**SDK "await a signal into a live run"**~~ — **DONE 2026-07-26**: `IWorkflowInputs.ReceiveAsync` (channel-fed from the response-queue subscription), registered by the WorkflowBuilder. `echo-decision-workflow` (Workflows.Testing) exercises the full propose→hold→decide→echo loop over the raw wire protocol.
- **The steerable workflow** — a `docker://` Auxilia workflow (`RequiresAiAgent`, egress policy, `DeclaresView("steering")`, `propose_action`/`request_scope` sink tools that block on P2-receive — the in-workflow mandate/hold, migrated from the deleted `SteeringSession`).
- **Per-repo working directory** — the run wizard's repositories have no working-directory concept; `/workspace/repos/{id}` is fixed by the WorkspaceManager and the agent cwd comes from `Workflow__WorkspaceDirectory`. Needs a `RepositorySpec.WorkingDirectory` (or agent-cwd run option) carried through dispatch → workspace → the coding-agent context.
- **Connector browse beyond GitHub** — `POST /api/connectors/{id}/browse` (live repo/branch lists, credential stays Core-side) implements GitHub only; TFS/Azure DevOps fall back to manual entry.
- **Zombie-run sweep** — a run whose terminal/failure event is lost (Core.Api or Core.Runner down at the wrong moment) lingers as Running: container-exit watchers do not survive a runner restart, and the FailoverMonitor only fails over runners it has SEEN heartbeat (in-memory). Needs: runner re-adopting (or reaping) its containers on start + a Core-side staleness transition (Running w/o owner heartbeat → Failed). Interim: `DELETE /api/runs?includeStale=true&staleMinutes=N`.
- **§4 credential-model decision (OPEN)** — Model A (Resource Proxy, token Core-side, build one `IResourceConnector` for git push — the doc's lean) vs Model B (scoped token in the sandbox).
- **the steering client cross-repo dependency → NuGet** — replace the side-by-side-clone `ProjectReference`s to `Auxilia.Core.Client`/`Contracts` (+ a standalone steering-codec package) with published packages.
- **the steering client desktop per-user sign-in** — replace the API-key principal with interactive (device-code/OIDC) sign-in that mints a per-user bearer (per-user audit/SoD). Needs a Core non-browser token-issue path.

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
