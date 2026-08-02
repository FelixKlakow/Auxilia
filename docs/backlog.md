# Auxilia — Backlog

> Live tracker of known follow-ups (open items only). Delivered programs and their decision
> records live in `docs/delivered/` and the git history.

## Core.Api — client-surface follow-ups
- **Per-user bearer hardening** — the short-lived bearer is embedded in the prerendered page
  (same-origin TLS); consider a server-side opaque-handle store keyed by principal.
- **Workflow-type registry administration** — register/approve/deny run via REST/MCP only;
  the AdminConsole has no registry-administration UI. The AI safety-check approval handler
  (static workflow over the submitted package) is designed but unbuilt.
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
  a public issue would expose the inquirer's company details). **BLOCKED 2026-08-02: Google
  flagged the freshly created licensing mailbox (felix.klakow.github@gmail.com — the address
  in both LICENSE files) right after signup; recover it (phone verification/appeal) or swap
  in a different receiving address, and verify the forward + send-as loop with a test mail
  BEFORE flipping public.** Then: final outside-eyes read of
  README/CONTRIBUTING, verify or retire `Start-Presentation.bat` (stale header comment, a
  "via Studio" help string in DevStand), optionally ask GitHub Support to GC the
  pre-rewrite objects, then flip the repository public. On flipping: set the repo
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
- **steering client desktop per-user sign-in** — replace the API-key principal with interactive
  (device-code/OIDC) sign-in that mints a per-user bearer (per-user audit/SoD). Needs a Core
  non-browser token-issue path.

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
- **Base-aware environment pickers** — the steering client/AdminConsole environment selection should
  constrain to one base once the first layer is picked (`ProviderCatalogEntry.EnvironmentBase`
  carries the data); today the Core rejects a mixed dispatch with a clear error.
- **Composed-image GC** for content-addressed `auxilia-env:<hash>` images.
- **Trust keys in real deployments** — `WorkflowDispatcher__TrustedEnvironmentSigningKeys`
  is empty (permissive) in dev; any real deployment needs the key material story.
- **Build-time hardening (context, 2026-08-02).** Environment composition runs `docker build`
  on a generated one-file Dockerfile (context = that file only; nothing from the host leaks
  in). The RUN steps execute in ordinary build containers: root inside, NO egress policy
  (the daemon's default build network — layers must download packages), no resource caps.
  The protection is deliberately WHO may author (admin-only + Core signature, verified by
  the runner before the build) rather than what the build may do. Future levers if needed:
  pin the build's NetworkMode to a network that reaches only the package proxy, and pass
  memory/CPU caps in `ImageBuildParameters`.

## Generic binding pipeline
- **AdminConsole editors on the generic model** — the Blazor editors still assume
  connector-only bindings and know nothing of provider-type narrowing, choice labels,
  environment slots, or the plan view.
- **Connector-grants editor in the steering client** — grants (principal / first-class group /
  directory group, batch-replace via `SetConnectorGrants`) are editable in the
  AdminConsole; the steering client's Connections tab has no grants surface yet.

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
- `ScreenshotHarness` stubbed (targeted the retired dashboard) — rewire to boot
  `Auxilia.AdminConsole` in `EndToEndEnvironment`.

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
