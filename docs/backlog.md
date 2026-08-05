# Auxilia — Backlog

> Live tracker of known follow-ups (open items only). Delivered programs and their decision
> records live in `docs/delivered/` and the git history.

## Core.Api — client-surface follow-ups
- ~~Per-user bearer hardening~~ — DONE 2026-08-03: the raw bearer no longer rides the
  prerendered page. The relay stashes it server-side (`UserBearerHandleStore`, singleton) and
  persists only a cryptographically random ONE-SHOT handle; the circuit redeems it exactly once,
  unredeemed entries expire after 2 minutes and never outlive the token. No raw-token fallback.
- ~~AdminConsole step-up prompt~~ — DONE 2026-08-03: `elevation-required` now opens an inline
  re-authentication panel on the Principals page; the successful step-up retries the pending
  mutation (the elevation header rides the circuit's client).
  ~~AdminConsole tags administration UI~~ — DONE 2026-08-05: the Principals page carries a
  Tags column (chip per tag with remove, inline add) over `SetPrincipalTagsAsync`, riding the
  same mutation/step-up seam as the other principal actions.
- ~~Environment catalog search~~ — DONE 2026-08-05: `GET /api/environment-layers` and
  `GET /api/environment-bases` accept `?search=` (case-insensitive substring over
  type/name, version(s), base, description), wired through `ICoreClient` and covered by
  component + client-surface system tests.
- **Workflow-type registry administration** — full client surface exists on `ICoreClient`
  (register/approve/deny/unregister), and the steering client ships a registry-administration panel
  (2026-08-02, permission-gated on `workflow-type.manage`/`workflow-type.sign`).
  ~~AdminConsole registry UI~~ — DONE 2026-08-03: `/admin/workflow-types` page (list with
  status/tags/lifetime, registration detail, register by package URI, approve/deny-with-reason
  gated on `workflow-type.sign`, enable/disable/unregister-with-confirm gated on
  `workflow-type.manage`; nav entry appears with either permission). No step-up panel: the Core
  does not elevation-gate registry mutations (only principal admin does).
  ~~AI safety-check approval handler~~ — DONE 2026-08-05 as the semantics-blind
  `verdict-workflow` pipeline handler: `CoreApi:ApprovalVerdictWorkflow` names an ACTIVE
  workflow type that is dispatched over each pending registration (context:
  `approval-workflow-type` / `approval-package-uri` / `approval-publisher-key` /
  `approval-registered-by`); the handler awaits the run and applies its `approval-verdict`
  artifact (`{"decision":"approve|deny","reason":…}`). Everything inconclusive (unconfigured,
  run failed, timeout, no/bad verdict) DEFERS to the human signing authority. The safety-check
  WORKFLOW itself (the AI review over the package) is authored like any other workflow —
  nothing Core-side remains. Note: a `core://`-stored pending package is not yet fetchable by
  the verdict run (no download authorization path); https/docker coordinates work today.
- **Session terminal remainders** — a console-mode Docker system test (stub CLI under tmux).
  ~~AdminConsole terminal surface~~ — DONE 2026-08-05: RunDetail shows "Open terminal" for a
  live terminal-hosting run (`RunStatus.HasTerminal`), mints the short-lived ticket via
  `OpenTerminalAsync`, and opens the Core's ticketed proxy URL in a new tab — the browser
  only ever talks to the Core.

## Hardening wave 2026-08-04 — dispatch truth, container re-adoption, stream/UI resilience
Delivered (see ARCHITECTURE §6/§14.2/§15): dispatch-time `Dispatched` run record + claim-timeout
sweep (`dispatch-never-claimed`) + rekey-on-claim + terminal sink; SSE snapshot-first-frame +
keepalives + `createdAfterUtc` artifact catch-up filter; resilient `Auxilia.Core.Client` streams
(`ClientStreamFrame` union, reconnect/backoff/idle-timeout/dedupe inside the client, unary
timeouts replacing `HttpClient.Timeout` — fixed the latent 100s stream-death); AdminConsole
`PagePoller`/`ConnectionBanner` (poll loops survive transport errors; RunDetail reconnects and
refetches; fixed the camelCase payload-decode bug); `ArtifactChainingEngine` catch-up + dedupe;
runner **container re-adoption** on restart (persisted container id + protected instance token,
re-claim before the failover clock, real exit collection, clean-kill fallback,
`ReadoptContainersOnStart` replaces `ReapWorkflowContainersOnStart`). Remaining follow-ups:
- **System tests (Docker + real RabbitMQ)** for the wave — PARTIALLY DONE 2026-08-05: the new
  `CoreClientSurface` fixtures cover the command-id-keyed SSE subscriber over real topic
  routing, snapshot-first, keepalive survival past 100s idle, and reconnect across a real
  Core.Api container restart. They immediately caught and fixed five real defects the fake
  bus can never show: (1) topic-binding RPCs shared the consumer's channel and could wedge it
  (bindings now ride a dedicated channel); (2) the run-stream publisher awaited the bind gate
  on the bus dispatch path (alias binds now drain through a worker); (3) a claim event beating
  a fresh command-keyed subscription orphaned the stream FOREVER — a periodic sweep now
  re-resolves unpaired command-id audiences from the run store and replays the record's
  current state, including terminal states (keepalives otherwise hold the orphan open);
  (4) the client's idle-timeout ABANDONED the in-flight read before disposing the response,
  which could hang the next resubscribe (the read is now properly cancelled); (5) a cancel
  racing the workflow's startup was published UNROUTED and silently dropped — the runner now
  declares the instance cancel queue before publishing, so the "hard stop" parks instead of
  vanishing. ~~Runner kill/restart, exit collection, clean-kill~~ — DONE 2026-08-05, all
  passing on real Docker: `ReAdoptionSystemTests` (runner restart mid-run → the SAME instance
  is re-adopted and completes; container SIGKILLed while the runner is down → Failed with the
  REAL exit code 137; a labeled container no record knows → clean-killed on restart) and
  `DispatchTimeoutSystemTests` (runner heartbeating but not consuming → `Dispatched` visible
  immediately, swept as `dispatch-never-claimed`).
- ~~Live verification against the dev stack~~ — superseded 2026-08-05: every scenario is now
  an automated system test (see above; mid-run Core restart → snapshot resume was already
  covered by the `CoreClientSurface` reconnect fixtures). Residual: a one-glance visual check
  of the RunDetail banner during a real Core restart — falls out of normal dev-stack use.

## Implementation workflow  ·  see `docs/implementation-workflow-design.md`
- **Real Copilot console events** — the Copilot CLI has no hooks; it does support
  `--log-dir`/`--log-level`. Run one real authenticated console session, inspect the log
  shape, then build the tail-based source feeding the existing `CopilotConsoleBridge` wire.
- **First real end-to-end run** against a live Azure DevOps story (requires a real tenant;
  the mocked-HTTP and simulated paths are verified).
- **Context management across shared stages** — summarization-on-demand when the window gets
  tight (today only the gate-idle `/compact` rule); a THIRD agent binding for cross-provider
  stage mixes beyond author+reviewer.
- **Externally configured MCP servers** passed into agent sessions (today only the
  workflow-hosted work-item server) — deferred, complicated.
- **Real-CLI verification** of the workflow-hosted work-item MCP server registration
  (Claude `.mcp.json` / Copilot `mcp-config.json` paths are unit-verified only).

## Coding-agent sessions — open verification
- **Copilot SDK session against the real service** — `CopilotSdkAgent` and the
  session-vocabulary (model + reasoning-effort) path are built and mock-verified; a live run
  needs a Copilot-entitled token.
- **Unexercised interactive paths** — an ask-mode permission card with suggestions, and a
  real-CLI push interception (unit/component-verified; needs a push-enabled repo binding).
- ~~Push-scoped token~~ — DONE 2026-08-05: connectors may carry an optional `push-token`
  secret (declared on `tfs-account`; any git-credential connector setting keyed
  `push-token`/`pushToken`/`push-pat` is honored — the resolver is key-based, no vendor
  logic). For an `AllowPush` mount the runner clones with the full credential and rewrites
  the container-visible origin to carry only the push-scoped token (ARCHITECTURE §9
  write-back control). Without a push token, behavior is unchanged.
- ~~Per-mount working directory in the agent context~~ — DONE 2026-08-02: a single mount's
  `Workflow__WorkspaceMount__<ID>` root (working-directory subpath included) is now the
  authoritative cwd in all three agent workflows; the repository slot's `WorkingPath` remains
  the seam for mount-less providers, and multi-mount runs fall back to the workspace root.

## Client libraries & packaging
- **Publish the NuGet packages** — pack metadata is done for `Auxilia.Core.Contracts`,
  `Auxilia.Core.Client`, `Auxilia.Workflows.Client`, and `Auxilia.Steering.Codec`
  (v0.1.0, BUSL license file, snupkg); all four verified with `dotnet pack` 2026-08-05
  (which caught and fixed a broken relative LICENSE path in every csproj — packing had
  never actually been run). Open: pick the feed (nuget.org vs private).
- ~~Steering codec extraction~~ — DONE 2026-08-05: `Auxilia.Steering.Codec` is the
  dependency-free wire-protocol library (typed `SteeringFrame` records + tolerant
  `SteeringCodec.Encode/Decode`); `OperatorChannel` and `ConsoleEventViews` now speak it
  instead of private wire records (wire JSON unchanged — locked by literal protocol tests),
  and desktop clients can reference it without the full contracts surface.
- **Go-public pre-flight** (repo is otherwise publish-ready: rewritten noreply-only history,
  single `main`, licenses + pricing incl. free personal tier; licensing contact is EMAIL —
  a public issue would expose the inquirer's company details). **Mailbox RESOLVED 2026-08-03
  (the flagged felix.klakow.github@gmail.com account is recovered/done). Remaining gate:
  Felix verifies the forward + send-as loop with a test mail himself and then explicitly
  decides to publish — do NOT flip public before that go.** (Pre-flight re-verified 2026-08-03: single `main`, noreply-only
  history, no product-external names in tracked files, no real secrets — only fake test
  tokens; `Start-Presentation.bat` is already retired and the stale "via Studio" DevStand
  string is fixed. The mailbox is the ONE open gate.) Then: optionally ask GitHub Support
  to GC the pre-rewrite objects, then flip the repository public. On flipping: set the repo
  description ("Self-hosted platform for governed AI agent workflows — signed containers,
  just-in-time scoped credentials, live operator steering, full REST + MCP parity") and
  topics (ai-agents, agentic-ai, workflow-engine, ai-orchestration, coding-agent,
  claude-code, mcp, model-context-protocol, self-hosted, dotnet, csharp, aspnetcore,
  blazor, rabbitmq, docker), register `.github/workflows/publish-nuget.yml` as the
  nuget.org Trusted Publishing workflow (tag-triggered `v*`; verify the nuget.org
  username in the workflow's `user:` input), and walk the repo-settings checklist:
  enable Discussions + private vulnerability reporting (SECURITY.md points there),
  Dependabot alerts, secret-scanning push protection; restrict Actions to the two
  used action publishers + read-only default GITHUB_TOKEN; a main ruleset blocking
  force-push/deletion with admin bypass; disable Wiki/Projects.
- ~~Steering-client desktop per-user sign-in~~ — DONE 2026-08-03 for the password path: the Core's
  `POST /auth/login` (username+password → the same per-user bearer the browser mints, desktop
  lifetime `CoreSecurity:LoginTokenLifetimeMinutes`, default one workday, audited both ways)
  plus the steering client sign-in overlay/header buttons (never a silent fallback to the service key
  once in user mode). Rate limiting on /auth/login DONE 2026-08-03: per-client-IP fixed window
  (`CoreSecurity:LoginRateLimitPermitsPerMinute`, default 5) plus a per-username failed-attempt
  throttle (`LoginFailureLimitPerUsername`/`LoginFailureWindowMinutes`, defaults 5/5) that a
  successful sign-in clears; excess attempts get a 429 and an `auth.login` rate-limited audit.
  Still open: the device-code/OIDC variant for Entra-only principals (SSO-provisioned humans
  have no password).

## Config store — DECIDED 2026-08-01: stays in the Core
The earlier "move the config store out of the Core" direction is reversed by decision, not
inertia: the Core is the one shared authority every client reaches, so Core-resident
configurations give cross-device/multi-client sharing (steering client on any machine, AdminConsole,
MCP agents see the same list) without inventing a product-side persistence service. This does
not violate semantics-blindness — a stored configuration is an opaque, schema-validated
document: the Core validates it against its schema registry, stores it, dispatches it via the
Run API, and never interprets what the workflow means (the same storage-vs-semantics division
as artifacts). **Per-configuration ownership + sharing DELIVERED 2026-08-01**: configurations
carry scope (Personal default, Company opt-in gated by `workflow-configuration.manage`), an
owner, and `AccessGrant`s (principal / first-class group / directory group — the same shared
model as connectors, evaluated by `AccessGrantEvaluator`); reads and the run path are
visibility-filtered, editing/deleting/sharing is owner-or-manager; surfaced in the
AdminConsole (scope column + Sharing editor) and the steering client (visibility choice in the
configure wizard, Sharing… on the workflow row menu). Group entries in workflow-type
access lists, `GET /api/roles`, and the `GET /api/directory/subjects` sharing directory
(pickers in both grant editors) shipped 2026-08-01; access lists are administered over MCP
(`list/grant/revoke_workflow_type_access`, gated `policy.administer`).

## Environment capabilities
- **Windows-container runners** — windows-base layers are stored but never served to Linux
  composition. (Mixed-base selections now fail fast at dispatch — 2026-08-01 — and each
  layer's base rides its catalog entry.)
- **Trust keys in real deployments** — `WorkflowDispatcher__TrustedEnvironmentSigningKeys`
  is empty (permissive) in dev; any real deployment needs the key material story.
- ~~Versioned bases~~ — DONE 2026-08-03 (ARCHITECTURE "Versioned bases"): admin-managed
  environment-base catalog ((name, version) pairs, `/api/environment-bases`, provider-catalog
  gated, full client surface), layers optionally pin a registered base version, the pin rides
  the catalog entry, and dispatch + authoring fail fast on mixed base versions like mixed
  base names. Capability-side versioning needs no mechanism — the open provider-type
  vocabulary already carries it (`dotnet-10` / `dotnet-8` are distinct capabilities).
  Remaining idea (unbuilt): bases carrying a concrete image reference the composition could
  `FROM` — today composition always builds FROM the workflow image, so an image ref on the
  base only becomes meaningful with pre-built environment containers.
- **Build-time hardening (context, 2026-08-02).** Environment composition runs `docker build`
  on a generated one-file Dockerfile (context = that file only; nothing from the host leaks
  in). The RUN steps execute in ordinary build containers: root inside, NO egress policy
  (the daemon's default build network — layers must download packages), no resource caps.
  The protection is deliberately WHO may author (admin-only + Core signature, verified by
  the runner before the build) rather than what the build may do. Future levers if needed:
  pin the build's NetworkMode to a network that reaches only the package proxy, and pass
  memory/CPU caps in `ImageBuildParameters`.

## Generic binding pipeline
- **Persistent workspaces — reset semantics (design note, 2026-08-03).** Today nothing
  persists per-run: every run gets an isolated copy materialized from the host-only warm
  repo cache (never bind-mounted) and the run root is deleted at terminal state, so
  cross-run leaks are impossible by construction. If a persistent/reusable workspace ever
  becomes first-class, the leak-proof reset is *re-materialization* (discard the run copy,
  copy afresh from the warm cache), not in-place `git clean`/`reset` — untracked/ignored
  files and hook side effects make in-place cleaning a leak channel. Any persistent
  workspace must be scoped to one owning identity (never shared across principals).
- ~~Post-binding setup scripts~~ — DONE 2026-08-03 (ARCHITECTURE §9): manifest-declared
  `RequiresRepository(..., setupScript:)` plus the mount-bound `setup-script` role; the runner
  announces (`Workflow__WorkspaceMountSetup__<ID>`), the SDK executes inside the container in
  the mount's root before the application, fail-fast, under the run's egress policy. Open:
  a Docker system test exercising a real in-container setup script.

- ~~Temporary/empty workspaces~~ — DONE 2026-08-03 (ARCHITECTURE §9): the `empty-workspace`
  provider — a data-only catalog descriptor (`mountsIntoWorkspace`, `working-directory` +
  `setup-script` roles, no credential contract; seeded by `Start-DevStack.ps1`) plus the
  runner's non-git materializer: a mount without a `clone-url` role becomes a fresh scratch
  directory under the same `repos/<mount-id>` layout, working directory pre-created, deleted
  with the run root at terminal state (cleanup was already materializer-agnostic). Nothing in
  the Core changed. Deliberately left out: artifact seeding — no natural fit yet (seed via the
  setup script for now; revisit with a dedicated artifact-input role if needed).

## CI validation — Docker system tests (not runnable locally)
- Email slot **plugin-dependency loading** (highest risk — MailKit/MimeKit/BouncyCastle
  copied alongside the provider DLL).
- ~~`/api/audit` response shape~~ — DONE 2026-08-05: `AuditEndpoint_ServesCamelCasePagedResult`
  in the CoreApiDispatch suite (passing locally).
- (2026-08-02: the whole system suite runs locally again. The Failover test reads the owner
  from the claim transition per the preserve-last-non-null contract; the legacy
  `CodeReviewWorkflow` fixture — dead since the seed-subsystem removal in `d582b5d` — was
  replaced by `EndToEnd/CodeReviewDispatchSystemTests` covering inline
  `ProviderType`+`Settings` bindings for happy AND write-back-failure runs; the ClaudeCode
  test dispatches `permission-mode=auto-allow`, since the steering-era ask-operator default
  otherwise blocks an autonomous run on a permission card — that was the "hang".)

## DevStand
- ~~`ScreenshotHarness` stubbed (targeted the retired dashboard) — rewire to boot
  `Auxilia.AdminConsole` in `EndToEndEnvironment`.~~ (DONE 2026-08-03: the AdminConsole now
  runs as a container in `EndToEndEnvironment` — new
  `Source/Platform/Auxilia.AdminConsole/Dockerfile`, `auxilia-admin-console:system-test`
  image, `Core__ApiKey` = a step-up-granted Administrator service key so pages render fully
  authenticated without a browser session — and the harness captures the console's twelve
  pages (`01-dashboard.png` … `12-audit.png`, dashboard live while the mail-triggered run is
  Running); the dev stand prints/opens the console URL again.)
- ~~AdminConsole under `dotnet run` serves broken static assets~~ — FIXED 2026-08-05: the
  project had no `launchSettings.json`, so `dotnet run` started in the Production
  environment where the static-web-assets dev manifest is never loaded — the Debug
  runtime-patching handler then 500'd on packaged `_content` files and served 0-byte
  `app.css`. A Development launch profile (`https://localhost:7299;http://localhost:5299`)
  fixes `dotnet run`; published output was and stays correct. Bonus hardening from the same
  investigation: `CoreBackedAuthenticationHandler` now degrades to anonymous instead of
  500-ing every request (static assets included) while the Core is unreachable.

## Core scalability — path to ~100k simultaneous clients
The shape is right (stateless Core.Api, SSE per node, competing-consumer runners, clients
never on the bus); the 2026-08-01 wave delivered shared-queue tracking mirrors, the auth
cache, signed terminal tickets, and the batched audit writer. **Selective event routing is
DELIVERED (2026-08-02)**: `workflow.status`/`workflow.views`/`workflow.artifacts` topic
exchanges (new names — the retired fanouts could not be redeclared), routing key stamped at
publish (status = `{instanceId}` or `{instanceId}.{commandId}` on the claim; views =
instance id; artifacts = sanitized artifact type), per-node dynamic bindings driven by the
SSE brokers' binding listeners (subscriptions gained Add/RemoveBinding; awaited on subscribe
so the no-event-lost-after-flush guarantee holds), command-id aliases resolved from the run
store for late subscribers, tracking mirrors bind `#`. ASB mapping (topic subscription
rules) remains a note for the ASB backend. Remaining:
- Infra per the ARCHITECTURE §14.4 table (RabbitMQ cluster / ASB, MongoDB backend). Note:
  100k clients ≠ 100k concurrent runs — the run axis is runner-fleet/container capacity plus
  heartbeat/failover-monitor volume, tracked separately.

## Deeper platform consolidation (from the separation plan)
- Core.Api + Core.Runner shared **Core DB tier** (currently separate DBs; several features
  bridge the split over the bus).
- **Endpoint-granular network-policy enforcement** — the resolver computes per-run
  `AllowedEndpoints` and the launcher realizes only the no-egress case at the Docker level
  (`--internal` network); per-endpoint enforcement needs the future EGRESS PROXY (a per-run
  HTTP(S)/DNS forward proxy the container's only route points at, filtering on the allowed
  list) — a design of its own, not an increment.
- ~~Run-API quotas~~ — DONE 2026-08-05: `CoreApi:RunQuotas` — `MaxActiveRuns` (platform-wide
  cap on non-terminal runs) and `MaxDispatchesPerPrincipalPerMinute` (per-principal fixed
  window; system dispatches without a principal — failover redispatch, the approval
  pipeline — are exempt). Enforced at the `RunService` dispatch chokepoint (inline, stored
  configuration, and rerun paths), rejected with 429 + a `run.quota-exceeded` audit entry;
  0 = unlimited (the default).
