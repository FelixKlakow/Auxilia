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
  mutation (the elevation header rides the circuit's client). Still open: the steering client's
  type-to-confirm pattern, and tags administration UI (steering client-only today).
- **Workflow-type registry administration** — full client surface exists on `ICoreClient`
  (register/approve/deny/unregister), and the steering client ships a registry-administration panel
  (2026-08-02, permission-gated on `workflow-type.manage`/`workflow-type.sign`).
  ~~AdminConsole registry UI~~ — DONE 2026-08-03: `/admin/workflow-types` page (list with
  status/tags/lifetime, registration detail, register by package URI, approve/deny-with-reason
  gated on `workflow-type.sign`, enable/disable/unregister-with-confirm gated on
  `workflow-type.manage`; nav entry appears with either permission). No step-up panel: the Core
  does not elevation-gate registry mutations (only principal admin does). The AI safety-check
  approval handler (static workflow over the submitted package) remains designed but unbuilt.
- **Session terminal remainders** — a console-mode Docker system test (stub CLI under tmux)
  and an AdminConsole terminal surface.

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
- **Push-scoped token** — push currently uses the connector's token as-is; a token scoped to
  push (e.g. fine-grained PATs) would narrow the blast radius.
- ~~Per-mount working directory in the agent context~~ — DONE 2026-08-02: a single mount's
  `Workflow__WorkspaceMount__<ID>` root (working-directory subpath included) is now the
  authoritative cwd in all three agent workflows; the repository slot's `WorkingPath` remains
  the seam for mount-less providers, and multi-mount runs fall back to the workspace root.

## Client libraries & packaging
- **Publish the NuGet packages** — pack metadata is done for `Auxilia.Core.Contracts`,
  `Auxilia.Core.Client`, and `Auxilia.Workflows.Client` (v0.1.0, BUSL license file, snupkg);
  open: pick the feed (nuget.org vs private) and extract the steering codec into its own
  package so desktop clients don't need the full contracts surface.
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
- ~~steering client desktop per-user sign-in~~ — DONE 2026-08-03 for the password path: the Core's
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
- `/api/audit` response shape (camelCase `PagedResult`).
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
- **AdminConsole under `dotnet run` serves broken static assets** — the Debug static-asset
  manifest's runtime-patching handler 500s on the packaged BlazorAgentView css and serves
  0-byte compressed bodies for `app.css` (page renders unstyled). The published build
  (`dotnet publish`) is correct — visual checks must run the published output.

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
- Endpoint-granular network-policy enforcement + **Run-API quotas** (hardening).
