# Auxilia — Backlog

> Live tracker of known follow-ups. Captured 2026-07-26 at the close of the BackendService retirement + the steering client first-client rework. Delivered programs live in `docs/delivered/`; this is what's *left*.

## Core.Api — client-surface follow-ups (surfaced during the retirement)
- ~~**Config *update* endpoint**~~ — **DONE 2026-07-27**: `PUT /api/configurations/{id}` + `PUT /api/connectors/{id}` (rename + key-wise settings upsert — the credential-refresh path), `ICoreClient.UpdateConfigurationAsync`/`UpdateConnectorAsync`; the steering client edits configurations (prefilled wizard) and connections (edit-in-place form; empty = keep stored value). AdminConsole editor still "saves as new".
- ~~**`PackageUri` per workflow type**~~ — **DONE 2026-07-26** with the workflow-type registry (ARCHITECTURE §7): types register permanently with their signed package; runs/configs are type-only, the Core resolves the coordinate, and `WorkflowTypeDto`/`WorkflowSchemaDto` carry `PackageUri` + `Status`. Follow-ups: Studio's authoring catalog should *register* its types into the Core instead of keeping its own `PackageUri`; AdminConsole has no registry-administration UI yet (register/approve/deny run via REST/MCP); the AI safety-check approval handler (static workflow over the submitted package) is a designed-but-unbuilt pipeline handler.
- **Trigger-CRUD API** — triggers live in Studio (headless); the AdminConsole editor links out. Decide console↔Studio vs Core-proxied, then build trigger create/edit/enable/disable.
- ~~**Persisted view-read**~~ — **DONE 2026-07-26**: `RunViewTrackingService` mirrors `ViewDataMessage` into Core-owned `CoreRunViewRecord` (capped per run), `GET /api/runs/{id}/views` + `ICoreClient.GetRunViewsAsync` read it back; the steering client backfills finished runs' outputs from it. AdminConsole RunDetail could now use the same read path (not yet wired there).
- ~~**Rerun endpoint**~~ — **DONE 2026-07-27**: `POST /api/runs/{id}/rerun` + `ICoreClient.RerunAsync` re-dispatches from the stored dispatch command as a NEW run (fresh command id + resolution token, bindings re-stashed — reusing the old token could never resolve credentials — connector eligibility re-checked, audited). steering client history rows have Rerun. NOTE: the failover re-dispatch still reuses the OLD token with a new command id — credentialed slots would fail to resolve on a failover redispatch; align it with RerunAsync.
- **Dashboard stats/pins** — no stats endpoint (counts computed client-side from a query page) and no persisted pinned dashboards.
- ~~**`RunStatus` completion time / duration**~~ — **DONE 2026-07-27**: `CoreRunRecord.CompletedUtc` stamped on the first terminal transition; `RunStatus.CompletedUtc`/`Duration` carried; steering client history shows the duration.
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
- ~~**Zombie-run sweep**~~ — **DONE 2026-07-27**: workflow containers are labeled (`auxilia.workflow=1`) and reaped at runner startup (label-scoped, single-runner-per-host); the FailoverMonitor additionally fails over non-terminal runs whose owner was NEVER heard from once their record is stale (grace = one heartbeat window after monitor start). Verified live: runner killed mid-run → restart reaped the container, the sweep failed the run. steering client also gained **Interrupt** (hard container stop via cancel; the dispatch-id alias in the cancel endpoint was fixed on the way).
- ~~**§4 credential-model decision**~~ — **DECIDED + BUILT 2026-07-27: Model B + interception**. `AllowPush` on the repository binding keeps the scoped credential on the (always-fresh) per-run clone; `git push` is classified as the PUSH action kind and governed by `push-policy` (ask/auto, independent of the general mode, live-changeable); clones get a provisioned commit identity (`CommitName`/`CommitEmail` binding settings, platform default otherwise). Open slivers: a token *scoped to push* (today it's the connector's token as-is — GitHub fine-grained PATs recommended); real-CLI live push verification (unit/component-verified so far); per-action policies for future kinds (deploy, mail) when they appear.
- **the steering client cross-repo dependency → NuGet** — replace the side-by-side-clone `ProjectReference`s to `Auxilia.Core.Client`/`Contracts` (+ a standalone steering-codec package) with published packages. Packaging note (2026-07-27): the steering client now requires the **WebView2 runtime** (BlazorAgentView conversation renderer) and targets `net10.0-windows10.0.19041.0` (Windows SDK projections).
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
- **AdminConsole/Studio editors on the generic model** — the steering client renders bindings from catalog descriptors; the Blazor editors still assume connector-only bindings and know nothing of provider-type narrowing, choice labels, environment slots, or the plan view (the AdminConsole's BlazorAgentView renderer predates plan/steering).
- ~~**Copilot interactivity**~~ — **BUILT 2026-07-27**: `CopilotSdkAgent` (GitHub.Copilot.SDK 1.0.8) gives Claude-parity — permission requests become decision cards (push classified via `FullCommandText`, per-action policy honored), the CLI's `OnUserInputRequest` questions become operator forms, guidance rides as follow-up messages, a live model switch maps to `SetModelAsync`, tool events stream as correlated chat cards. Opt-in per connector (`UseSdkSession`); the line mode stays for stubs/system tests. Plugins can now ship their NuGet closure (`BundleDependencies` in the manifest + `CopyLocalLockFileAssemblies`; runner bundles sibling non-Auxilia DLLs into the container — also de-risks the email plugin). OPEN: a live SDK-session run needs a Copilot-entitled token (Connect… captures one; untested against the real service).
- ~~**Real-CLI interactive verification**~~ — **DONE 2026-07-27**: two real `claude` sessions (Felix's account) verified AskUserQuestion → `updatedInput.answers`, permission auto-allow, the live permission-mode switch, JIT OAuth refresh (forced-stale token → refreshed at delivery → run Success), and plan capture. Finding: current CLIs use **TaskCreate/TaskUpdate**, not TodoWrite — the plan tracker folds both vocabularies into snapshots now. Still unexercised for real: an ask-mode permission card with suggestions, and push interception (needs a push-enabled repo binding).
- ~~**OAuth refresh-token capture**~~ — **DONE 2026-07-27**: the connect flow captures refresh token + expiry alongside the access token; catalog entries declare a data-driven `ProviderOAuthRefresh` spec (endpoint, client id, setting keys — the Core stays provider-agnostic); `ConnectorTokenRefresher` refreshes at JIT delivery AND before connector browsing, persisting rotated values. Verified live with a forced-stale token.

## Shipped 2026-07-27 without a prior backlog entry (for the record)
Slot **provider-type narrowing** (`Requires<T>(providerTypes:)`, Core-enforced at dispatch);
connector/configuration **update endpoints** + steering client edit-in-place (credential refresh);
**permission modes** + per-action **push policy** (live-changeable session settings incl. model
switch); **first-class permission decisions** (detail block, permission suggestions →
`updatedPermissions`); **AskUserQuestion** answers via `updatedInput`; **plan view**
(TodoWrite / checklist → live checklist); **BlazorAgentView** conversation in the steering client;
runner **plugin/image compatibility pre-flight**; pre-start launch failures now fail runs
visibly instead of stranding them at Received.

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
