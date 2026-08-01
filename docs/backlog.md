# Auxilia — Backlog

> Live tracker of known follow-ups. Captured 2026-07-26 at the close of the BackendService retirement + the steering client first-client rework. Delivered programs live in `docs/delivered/`; this is what's *left*.

## Core.Api — client-surface follow-ups (surfaced during the retirement)
- ~~**Config *update* endpoint**~~ — **DONE 2026-07-27**: `PUT /api/configurations/{id}` + `PUT /api/connectors/{id}` (rename + key-wise settings upsert — the credential-refresh path), `ICoreClient.UpdateConfigurationAsync`/`UpdateConnectorAsync`; the steering client edits configurations (prefilled wizard) and connections (edit-in-place form; empty = keep stored value). AdminConsole editor still "saves as new".
- ~~**`PackageUri` per workflow type**~~ — **DONE 2026-07-26** with the workflow-type registry (ARCHITECTURE §7): types register permanently with their signed package; runs/configs are type-only, the Core resolves the coordinate, and `WorkflowTypeDto`/`WorkflowSchemaDto` carry `PackageUri` + `Status`. Follow-ups: Studio's authoring catalog should *register* its types into the Core instead of keeping its own `PackageUri`; AdminConsole has no registry-administration UI yet (register/approve/deny run via REST/MCP); the AI safety-check approval handler (static workflow over the submitted package) is a designed-but-unbuilt pipeline handler.
- ~~**Trigger-CRUD API**~~ — **DISSOLVED 2026-08-01** by the Studio→library program: trigger definitions are host-owned (`Auxilia.Workflows.Client` `ITriggerStore`); a host exposes whatever admin surface it wants.
- ~~**Persisted view-read**~~ — **DONE 2026-07-26**: `RunViewTrackingService` mirrors `ViewDataMessage` into Core-owned `CoreRunViewRecord` (capped per run), `GET /api/runs/{id}/views` + `ICoreClient.GetRunViewsAsync` read it back; the steering client backfills finished runs' outputs from it. AdminConsole RunDetail could now use the same read path (not yet wired there).
- ~~**Rerun endpoint**~~ — **DONE 2026-07-27**: `POST /api/runs/{id}/rerun` + `ICoreClient.RerunAsync` re-dispatches from the stored dispatch command as a NEW run (fresh command id + resolution token, bindings re-stashed — reusing the old token could never resolve credentials — connector eligibility re-checked, audited). steering client history rows have Rerun. NOTE: the failover re-dispatch still reuses the OLD token with a new command id — credentialed slots would fail to resolve on a failover redispatch; align it with RerunAsync.
- **Dashboard stats/pins** — no stats endpoint (counts computed client-side from a query page) and no persisted pinned dashboards.
- ~~**`RunStatus` completion time / duration**~~ — **DONE 2026-07-27**: `CoreRunRecord.CompletedUtc` stamped on the first terminal transition; `RunStatus.CompletedUtc`/`Duration` carried; steering client history shows the duration.
- ~~**Session terminal**~~ — **DONE 2026-07-28**: the authenticated Core terminal proxy replaced the retired `SessionTerminalProxy`. Launcher publishes per `TerminalPublishMode` (loopback ephemeral 127.0.0.1 port for host-process Cores — the default — or container-network for containerized ones); the endpoint rides the Queued status event into `CoreRunRecord` (never to clients); `run.open-terminal` (Admin+Operator) gates `POST /api/runs/{id}/terminal-ticket` (audited, short-lived multi-use ticket) and the `/terminal/` proxy forwards HTTP + websocket to ttyd (ticket via query or path-scoped cookie). `RunStatus.HasTerminal`, `ICoreClient.OpenTerminalAsync`, and the steering client's Terminal button/WebView2 window complete the loop. Live-verified through the proxy (ttyd page + websocket handshake + auth rejections). Remaining: a console-mode Docker system test (stub CLI under tmux), an AdminConsole terminal surface, and interactive click-through of the steering client window (wired, compile-verified, not yet hand-tested).
- **Built-in role list endpoint** — the AdminConsole hard-codes the role list (no Core list-roles); actor→display-name resolution in Audit ties to a principals lookup.
- **Legacy `ConnectorRecord` cleanup** — superseded by `CoreConnectorRecord`; verify no consumer, then remove.
- **Per-user bearer hardening** — the short-lived bearer is embedded in the prerendered page (same-origin TLS); consider a server-side opaque-handle store keyed by principal.

## Implementation workflow (docs/implementation-workflow-design.md, started 2026-07-28)
- **P0–P2 DELIVERED 2026-07-28** (commit c3b97f9): Copilot console mode; `IWorkItemAccess` states + NEW `Auxilia.Slots.AzureDevOps` (tfs-account work items, 16 HTTP-mocked tests); `SendTextAsync`/`DrivenConsoleSession`/`ConsoleEventViews`; the `implementation` workflow (pipeline + gates + idle-gate compaction + review runners + push + story state; 3 pipeline unit tests), old-generation `Auxilia.ImplementationWorkflow` + its fakes DELETED. Type registered ACTIVE in the dev Core with `docker://auxilia-implementation-workflow:system-test` + emitted schema. NOTE: the dev runner needs `WorkflowLauncher__SlotPackages__tfs-account=<repo>\Auxilia.Slots.AzureDevOps\bin\Debug\net10.0\Auxilia.Slots.AzureDevOps.slothandler.dll`.
- **P3 mostly delivered 2026-07-28 late**: driven-capable stub (`driven-stub.sh`) + full-pipeline Docker system test **GREEN 2026-07-30** (ImplementationWorkflowSystemTests, both tests): the historic "timeout" was TWO real bugs — the e2e runner never approved long-living types (`WorkflowDispatcher__ApprovedLongLivingWorkflowTypes`), and the email plugin's own `Auxilia.Adapters.Email` was never bundled into the workflow container (BundleDependencies excludes Auxilia-prefixed DLLs; new manifest opt-in `BundledAuxiliaAssemblies` names a plugin's own adapters). A second test (`SimulatedStory_StateGateAnswered_WalksTheDeclaredFlow`) automates the steering client's [SIM] scenario: simulated work items, driven stub, state gate answered over the raw steering wire, declared-flow stage order asserted. Base prompts per role (`base-prompt`, `author-base-prompt`/`reviewer-base-prompt`; Claude `--append-system-prompt`, others first-prompt), markdown gate details + MERMAID diagrams (AgentView.Wpf FenceRenderer + hub WebView2 renderer), `Browse: "models"` on both CLI Model settings (live model dropdown at setup), run/config forms grouped (defaulted inputs fold into "Advanced"), and the clickable step flow (`WorkflowStepFlow` "flow" view → steering client chips) all landed. NOTE: after schema-affecting input changes, RE-REGISTER types (`--emit-schema` → POST /api/workflow-types + approve) or the steering client shows stale inputs. Copilot as the DRIVEN author works since 2026-07-30: `CopilotConsoleBridge` (hook-wire loopback listener + `~/.auxilia/console-hook-port` announcement, registered on the coding-agent slot) gives the pipeline turn completion; the driven stub falls back to the port file, and `Demo-ImplementationAgents.ps1` seeds SIM configurations for BOTH authors. Remaining: REAL Copilot console events (CLI has no hooks; CONFIRMED it has `--log-dir`/`--log-level` — run one real authed console session, inspect the log shape, then build the tail-based source feeding the same bridge wire); first REAL end-to-end run against a live AzDO story (needs Felix's tenant). Markdown-rendered gate details DELIVERED 2026-07-28 (`OperatorQuestion.DetailFormat` "markdown" → AgentView.Wpf MarkdownViewer in the decision cards; permission tool inputs stay monospace).

- **Declared flow (presentation metadata) DELIVERED 2026-07-29**, reworked same day per Felix's feedback into GENERAL declared view data: `ViewDescriptor.DeclaredDataJson` (opaque, renderer-key-scoped packaging-time content — no bespoke schema field) with the `flow` view (`step-flow` renderer key) declaring `FlowStepDescriptor(Id, Label, Description, SkipInput, SkipValue, Inputs)`; the runtime `flow` view slimmed to per-step states (`WorkflowStepState`), joined client-side. The steering client renders the stage view in the CONFIGURE and RUN panels (skip hints dim live; a skipped step's exclusive inputs drop out of the form, gating inputs stay) and the monitor joins live states (shared `FlowStrip` stepper control). Dev stack scripts added: `Start-DevStack.ps1` (the launch recipe, previously session-memory only) + `Cleanup-TestWorkflows.ps1` (testing configurations/types/finished runs — run after test sessions so the steering client stays clean; executed 2026-07-29, echo/sleeping/crashing/steering-sample family removed from the dev Core). Direction: more presentation metadata (input grouping/ordering) can follow the same schema-declared pattern instead of per-workflow frontend code. All three workflow images (implementation/claude-code/copilot) rebuilt for the new flow shape 2026-07-29 (after a transient router-DNS outage on mcr.microsoft.com).

## Implementation workflow — feedback batch 2026-07-30 (Felix)
- ~~**Review-bundle presentation**~~ — **DONE 2026-07-30**: porcelain codes translate to
  readable labels (`ImplementationPipeline.DescribeChanges`); NOT an encoding issue (verified).
- ~~**Rename the `completeness` stage → `refinement`**~~ — **DONE 2026-07-30** (step id, input
  `refinement-check`, context, prompts, stub, seeds, system tests, docs). Schema-affecting:
  re-register the type + rebuild the image.
- ~~**Agent sharing across stages**~~ — **DONE 2026-07-30**: the author console was ALREADY
  shared across refinement→plan→implementation; now per-stage overrides exist —
  `plan-agent`/`implement-agent` (shared/fresh/reviewer; `AgentConsolePool`, one event source
  fanned out via `ConsoleSessionEventHub`, secondary tmux sessions killed session-scoped) and
  `review-by` (reviewer/refinement-agent — the refiner reviews with its full context).
  Follow-ups: summarization-on-demand for a tight window (today: the gate-idle `/compact`
  rule); a THIRD agent binding for cross-provider stage mixes beyond author+reviewer.
- ~~**Default per-stage instructions**~~ — **DONE 2026-07-30**: `refinement-instructions` /
  `plan-instructions` / `implement-instructions` / `review-instructions` (multiline inputs,
  schema default = `ImplementationPrompts`; pipeline appends the fixed `.auxilia/` contract).
- ~~**Plan artifacts**~~ — **DONE 2026-07-30**: default plan instructions demand a mermaid
  `## Design` section; plan.md is copied to the run outputs the moment it exists (reviewable
  at the plan gate, not only after implementation).
- ~~**Work-item MCP**~~ — **DONE 2026-07-30**: `IWorkItemAccess.GetRelationsAsync` (+
  `WorkItemRelation`; AzDO maps Hierarchy/Related/Dependency/Hyperlink rels with best-effort
  titles), `WorkItemAccessMcpTools` gained `get_work_item_relations` + `get_work_item_states`,
  and the implementation workflow HOSTS the server (loopback HTTP) for the driven console:
  `IConsoleSessionPreparer.RegisterMcpServerAsync` (Claude: workspace `.mcp.json` +
  `enableAllProjectMcpServers`; Copilot: `~/.copilot/mcp-config.json`). `.auxilia/` and
  `.mcp.json` are repo-locally git-excluded (clean bundles; the final commit never picks them
  up). Real-CLI live verification pending. Later, separately: externally configured MCP
  servers passed into sessions — deferred, complicated.

## steering client — feedback batch 2026-07-30 (steering client repo)
- **Step click-through** — clicking a finished workflow step opens that step's data (the last
  view data available for it), including executed plans.
- **Live wall** — a "remove finished/old agents" button; tiles KEEP their position; agent
  selection moves onto the single tile (dropdown at the tile: active agents first, finished
  sessions after).
- **Implementation run setup** — show the advanced settings inline (the settings count shrank;
  the extra fold/panel is no longer worth it).

## Workflow Studio → client library (DECIDED + DELIVERED 2026-08-01)
**DONE except packaging:** `Auxilia.WorkflowStudio` is deleted. `Auxilia.Workflows.Client`
(authoring + interval scheduler + artifact chaining over the filtered artifact SSE, host-
pluggable `ITriggerStore`) replaced it; `Source/Auxilia.TriggerHost` is the always-on
reference host (+ email intake) and took Studio's place in the EndToEnd system test (no bus
env at all). The Studio type catalog died — the Core registry is the only catalog; the
Trigger-CRUD backlog item dissolved into the library's `ITriggerStore` API. NOTE: the
"mailbox credential → Core connector" idea was DROPPED as impossible by design — connector
secrets never leave the Core, so client-side intake credentials are host configuration.
Original decision record follows; remaining slice = the NuGet packaging pass below.

Division of responsibility (Felix confirmed): **artifact storage/management stays in
the Core** (persistence at run completion, store backends, metadata, retention/access
policy, event publication); **chaining and triggering are client/library tasks** (what
follows what is workflow-domain policy; the Core validates and executes).

- ~~**Core prerequisite — artifact events + reads on the client surface.**~~ — **DONE
  2026-08-01**: `GET /api/artifacts` (+ `/{id}`, `/{id}/content`, `/stream`), all gated
  `artifact.consume`; metadata mirrored bus→`CoreArtifactRecord` (`ArtifactTrackingService`),
  SSE via `ArtifactStreamBroker` with SERVER-SIDE type/work-item filters; payloads from the
  shared `ArtifactStore:PayloadRoot` backend (`IArtifactPayloadReader`; dev stack wires both
  services to `.devstack\artifacts`); `ICoreClient` Query/Get/OpenContent/StreamArtifactEvents;
  MCP `list_artifacts`/`get_artifact`; `ArtifactPersistedEvent` gained `SizeBytes`.
- **Library scope** (working name `Auxilia.Workflows.Client` or similar): authoring/dispatch
  helpers (fetch schema, validate bindings, resolve connectors, configure + run in one
  call — today's `WorkflowAuthoringService`), the trigger engine (interval scheduler +
  artifact-chaining, embeddable as hosted services or a manually pumped loop for desktop
  hosts), and the email work-item intake adapter (moving its mailbox credential from the
  legacy `SlotInstanceRecord` to a Core connector on the way). Studio's type catalog is NOT
  carried over — the Core workflow-type registry is the only catalog.
- **Retire with Studio:** the standalone Product MCP (Core MCP already covers runtime; an
  authoring MCP can return as part of a host app later), `StudioWorkflowTypeRecord`,
  `/api/configure` + `/api/workflow-types` REST, the Studio DB.
- **Ties into:** client-library NuGet packaging (pack + publish `Auxilia.Core.Contracts`,
  `Auxilia.Core.Client`, the steering codec, and the new library), the config-store-ownership
  item below (the library becomes the natural home of "the product owns configs"), and the
  Trigger-CRUD backlog item above (trigger storage becomes host-owned; the CRUD question
  dissolves into the library's API).

## Config-store ownership (Felix's model — deferred as its own step)
- **Move the persisted config store out of the Core.** "The Core doesn't own persisted workflows." Today `/api/configurations*` + `RunConfigurationAsync` still live in Core (tangled with the `CoreApiDispatch` acceptance test). Target: the product owns the config store; the Core validates a submitted spec against its **schema registry** and runs it via the Run API.

## Human-steering — the full loop (the steering client)  ·  see `docs/steering-client-integration.md`
- ~~**Deliver-input (P2)**~~ — **DONE 2026-07-26**: `POST /api/runs/{id}/inputs` + `ICoreClient.ProvideInputAsync` (authorizes `run.provide-input`, audits, publishes `WorkflowInputMessage` to the instance's dedicated INPUT queue `workflow-response-{id}-inputs`; accepts dispatch-command or instance id — a standing subscriber on the main response queue would compete with slot-activation/configuration responses, found + fixed 2026-07-27). steering client guidance/approve/reject/halt are live.
- ~~**SDK "await a signal into a live run"**~~ — **DONE 2026-07-26**: `IWorkflowInputs.ReceiveAsync` (channel-fed from the response-queue subscription), registered by the WorkflowBuilder. `echo-decision-workflow` (Workflows.Testing) exercises the full propose→hold→decide→echo loop over the raw wire protocol.
- ~~**The steerable workflow**~~ — **DONE 2026-07-27**: the SDK gained `OperatorChannel` (capabilities, question forms, guidance, halt, session-ended over the steering wire protocol) and the coding-agent session engine (`AgentSessionApplication`, shared by `claude-code` AND `github-copilot`) bridges agent questions to operator forms. The Claude provider runs interactively (`--input-format stream-json`, `can_use_tool` control requests decided by the operator, guidance injected as user turns); verified live end to end with the stub.
- ~~**Per-repo working directory**~~ — **DONE 2026-07-27** (as data): `git-repository` declares a `WorkingDirectory` setting with role `working-directory`; each mount's effective root (clone + working dir) is announced as `Workflow__WorkspaceMount__<ID>`. Open sliver: the coding-agent context still uses `ISourceControlAccess.WorkingPath`/`Workflow__WorkspaceDirectory` for its cwd — consume the per-mount variables there.
- ~~**Connector browse beyond GitHub**~~ — **BUILT 2026-07-27**: any connector carrying an `OrgUrl` setting browses via the Azure DevOps Git REST API (repos org-wide as Project/Name, branch heads resolved from the clone URL by remote-URL match with a name fallback; PAT basic auth — cloud and on-prem TFS alike). `tfs-account` gained the required `OrgUrl` setting. Verified against a MOCKED HTTP layer with real ADO response shapes (no official ADO container exists — Windows-only, licensed), asserting exact URLs + auth header; awaiting Felix's real-tenant connectivity check.
- ~~**Zombie-run sweep**~~ — **DONE 2026-07-27**: workflow containers are labeled (`auxilia.workflow=1`) and reaped at runner startup (label-scoped, single-runner-per-host); the FailoverMonitor additionally fails over non-terminal runs whose owner was NEVER heard from once their record is stale (grace = one heartbeat window after monitor start). Verified live: runner killed mid-run → restart reaped the container, the sweep failed the run. steering client also gained **Interrupt** (hard container stop via cancel; the dispatch-id alias in the cancel endpoint was fixed on the way).
- ~~**§4 credential-model decision**~~ — **DECIDED + BUILT 2026-07-27: Model B + interception**. `AllowPush` on the repository binding keeps the scoped credential on the (always-fresh) per-run clone; `git push` is classified as the PUSH action kind and governed by `push-policy` (ask/auto, independent of the general mode, live-changeable); clones get a provisioned commit identity (`CommitName`/`CommitEmail` binding settings, platform default otherwise). Open slivers: a token *scoped to push* (today it's the connector's token as-is — GitHub fine-grained PATs recommended); real-CLI live push verification (unit/component-verified so far); per-action policies for future kinds (deploy, mail) when they appear.
- **steering client cross-repo dependency → NuGet** — replace the side-by-side-clone `ProjectReference`s to `Auxilia.Core.Client`/`Contracts` (+ a standalone steering-codec package, + the new `AgentView.Wpf` chat renderer at `C:\Users\Felix\source\repos\AgentView.Wpf`) with published packages. Packaging note updated 2026-07-28: the WebView2/BlazorAgentView island was replaced by the native AgentView.Wpf renderer — no WebView2 runtime needed, plain `net10.0-windows` TFM again. **2026-08-01: pack metadata DONE** for `Auxilia.Core.Contracts` / `Auxilia.Core.Client` / `Auxilia.Workflows.Client` (v0.1.0, BUSL license file — BUSL-1.1 is not an OSI SPDX expression so `PackageLicenseFile` it is; symbols as snupkg; `dotnet pack` verified). REMAINING: Felix picks the feed (nuget.org vs private — these are the product's control-plane SDK, unlike BlazorAgentView) and the steering-codec extraction.
- **steering client desktop per-user sign-in** — replace the API-key principal with interactive (device-code/OIDC) sign-in that mints a per-user bearer (per-user audit/SoD). Needs a Core non-browser token-issue path.

## Environment capabilities (decided + BUILT 2026-07-27; reworked to ADMIN-MANAGED SCRIPTS the same evening)
Felix's model, delivered and then centralized: an environment is a Core-stored record — base
environment (`linux`/`windows`, open vocab) + **initialization script** (+ optional pinned
Version, the seam for later pre-built versioned containers). Admin endpoints
`/api/environment-layers` (gated `provider-catalog.manage`) upsert the record AND its available
catalog entry; the Core generates a base64-wrapped RUN fragment from the script and **signs it
with the platform signing key** (build-time code = workflow-package trust bar); runners fetch it
at dispatch (resolution-token authorized), verify against
`WorkflowDispatcher__TrustedEnvironmentSigningKeys` (empty = permissive dev), and compose
content-addressed (`auxilia-env:<hash>`, hash now includes the BASE IMAGE DIGEST — stale-
composition bug fixed). `dotnet-10`/`node-22` migrated to Core-managed scripts; the static
`WorkflowLauncher__EnvironmentLayers` config remains the host-level override (system tests).
The steering client has the admin editor (Environments tab, Administrator role). Still open:
Windows-container runners (windows-base layers are stored but never served to Linux
composition), composed-image GC, trust keys in any real deployment.

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

## Shipped 2026-07-27 evening (multi-turn + steering client wave)
**Multi-turn agent sessions** (`multi-turn` Boolean input on `claude-code` AND `github-copilot`;
instruction optional; steering gained `turn-ended` out + `end` in via capability "end"; Claude
keeps stdin open across result events, Copilot loops `SendAndWaitAsync` with
guidance-as-next-prompt; headless Copilot CLI refuses multi-turn — needs `UseSdkSession`).
Verified live by Felix. Chat noise removed (init/result events publish no entries — failures
only). **Data-driven provider model catalogs** (`ProviderModelCatalog` on the registration —
endpoint/credential-mapping/response shape; browse kind `models`; Anthropic spec registered as
data; NOTE: the Claude *account* OAuth token got 401 from `/v1/models` — API-key connectors list
fine; fix by re-POSTing the spec's header combo). **Session vocabulary** (`session-vocabulary`
steering wire: models + per-model reasoning efforts; Copilot fills it from `ListModelsAsync`
and applies the new `reasoning-effort` setting via `SetModelAsync` — OPEN: untested against the
real Copilot service, same token caveat as the SDK session itself). The steering client (local, not in this
repo): live-agents multi-view (full-view tiles incl. plan/decisions/steering, agent picker,
layout presets, multi-window), decision routing to the OWNING monitor, dashboard favorites +
folders + persisted UI state, transcript export/copy, dark-themed chat, Environments admin tab.
CLAUDE.md gained the standing rule: **the Core has NO custom/vendor logic** — provider
knowledge only in dynamically registered data/plugins.

Follow-ups landed 2026-07-28 early: **detail-tag contract** (`AgentChatEntry.DetailTag` — the
workflow tags additional entries default-hidden; clients render generic per-tag show toggles;
tagged today: permission narration + plan/task bookkeeping incl. `ExitPlanMode`, whose approved
plan now feeds the plan view); turn boundaries no longer produce chat entries (steering-only);
chat follow-scroll enforced in the WebView host; wall layout = HARD tile capacity (◻/◫◫/2×2/2×3,
Agents ▾ picks which runs fill the slots; tabs uncapped); wall tiles carry plan + decision
cards (answers route to the OWNING monitor).

## CI validation — Docker system tests (Phase-4 assumptions, not runnable locally)
- Email slot **plugin-dependency loading** (highest risk — MailKit/MimeKit/BouncyCastle copied alongside the provider DLL).
- Core resolves fake code-review slots from inline `ProviderType`+`Settings` bindings.
- Declared-slot set of `pull-request-code-review` matches the six seeded bindings.
- Studio null-protector format (seeded `ProtectedSettingsJson` as plain JSON).
- `/api/audit` response shape (camelCase `PagedResult`); Failover timing (8s timeout / 2s scan; owner stamped on the status event; re-dispatch queue).

## DevStand
- `ScreenshotHarness` stubbed (targeted the retired dashboard) — rewire to boot `Auxilia.AdminConsole` in `EndToEndEnvironment`.

## Core scalability — path to ~100k simultaneous clients (assessed 2026-08-01)
The shape is right (stateless Core.Api, SSE per node, competing-consumer runners, clients
never on the bus); the blockers are implementation-level:
- **Selective event routing** — `RunStreamPublisher` consumes the status/view FANOUT on every
  node (per-node ingest = global event volume). Move to topic routing keyed by run id with
  dynamic bindings for runs that have open streams. The new artifact-event SSE (Studio→library
  program) must be born filtered (artifact type + principal scope) for the same reason.
- **Tracking mirrors → competing consumers** — `RunTrackingService`/`RunViewTrackingService`
  are fanout subscribers; N API nodes process and write every event N times. They are bus→DB
  mirrors and belong on queues (exactly-once processing), not fanouts.
- **Auth caching** — bearer→principal→policy resolves against the store per request (no cache
  in Core.Api). Add a short-TTL principal/policy cache invalidated on principal-admin writes.
- **Terminal tickets are node-local** (`TerminalTicketService` ConcurrentDictionary) — breaks
  behind a multi-node LB; needs a shared store or signed self-validating tickets.
- **Audit appends are synchronous inline** — needs a batched async writer + time-partitioned
  storage before high request rates.
- Infra per the ARCHITECTURE §14.4 table (RabbitMQ cluster / ASB, MongoDB backend). Note:
  100k clients ≠ 100k concurrent runs — the run axis is runner-fleet/container capacity plus
  heartbeat/failover-monitor volume, tracked separately.

## Deeper platform consolidation (from the separation plan)
- Core.Api + Core.Runner shared **Core DB tier** (currently separate DBs; several features bridge the split over the bus).
- Endpoint-granular network-policy enforcement + **Run-API quotas** (hardening).
