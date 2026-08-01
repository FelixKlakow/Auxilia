# Auxilia — Backlog

> Live tracker of known follow-ups (open items only). Delivered programs and their decision
> records live in `docs/delivered/` and the git history.

## Core.Api — client-surface follow-ups
- **Failover re-dispatch credential alignment** — the failover monitor re-dispatches with the
  ORIGINAL resolution token and a new command id, so credentialed slots would fail to resolve
  on a failover redispatch. Align it with the rerun path (`POST /api/runs/{id}/rerun`), which
  correctly mints a fresh token and re-stashes bindings.
- **Dashboard stats/pins** — no stats endpoint (counts computed client-side from a query page)
  and no persisted pinned dashboards.
- **Built-in role list endpoint** — the AdminConsole hard-codes the role list (no Core
  list-roles); actor→display-name resolution in Audit ties to a principals lookup.
- **Legacy `ConnectorRecord` cleanup** — superseded by `CoreConnectorRecord`; verify no
  consumer, then remove.
- **Per-user bearer hardening** — the short-lived bearer is embedded in the prerendered page
  (same-origin TLS); consider a server-side opaque-handle store keyed by principal.
- **Workflow-type registry administration** — register/approve/deny run via REST/MCP only;
  the AdminConsole has no registry-administration UI. The AI safety-check approval handler
  (static workflow over the submitted package) is designed but unbuilt.
- **AdminConsole persisted view-read** — RunDetail still streams only; wire it to
  `GET /api/runs/{id}/views` like the other clients.
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
- **Per-mount working directory in the agent context** — the coding-agent context still uses
  `ISourceControlAccess.WorkingPath`/`Workflow__WorkspaceDirectory` for its cwd; consume the
  per-mount `Workflow__WorkspaceMount__<ID>` variables instead.

## Client libraries & packaging
- **Publish the NuGet packages** — pack metadata is done for `Auxilia.Core.Contracts`,
  `Auxilia.Core.Client`, and `Auxilia.Workflows.Client` (v0.1.0, BUSL license file, snupkg);
  open: pick the feed (nuget.org vs private) and extract the steering codec into its own
  package so desktop clients don't need the full contracts surface.
- **steering client desktop per-user sign-in** — replace the API-key principal with interactive
  (device-code/OIDC) sign-in that mints a per-user bearer (per-user audit/SoD). Needs a Core
  non-browser token-issue path.

## Config-store ownership (deferred as its own step)
- **Move the persisted config store out of the Core.** The Core shouldn't own persisted
  workflows. Today `/api/configurations*` + `RunConfigurationAsync` still live in Core
  (tangled with the `CoreApiDispatch` acceptance test). Target: the product owns the config
  store; the Core validates a submitted spec against its **schema registry** and runs it via
  the Run API.

## Environment capabilities
- **Windows-container runners** — windows-base layers are stored but never served to Linux
  composition.
- **Composed-image GC** for content-addressed `auxilia-env:<hash>` images.
- **Trust keys in real deployments** — `WorkflowDispatcher__TrustedEnvironmentSigningKeys`
  is empty (permissive) in dev; any real deployment needs the key material story.

## Generic binding pipeline
- **OS-sensitive `DockerSocketPath` default** — Docker Desktop 29.4.2 broke the unix-socket
  default on Windows hosts; local runners need
  `WorkflowLauncher__DockerSocketPath=npipe://./pipe/docker_engine` (dev stack sets it).
- **AdminConsole editors on the generic model** — the Blazor editors still assume
  connector-only bindings and know nothing of provider-type narrowing, choice labels,
  environment slots, or the plan view.
- **Connector-grants editor UI** — grants (principal / first-class group / directory
  group, batch-replace via `SetConnectorGrants`) are API/MCP-only; neither the
  AdminConsole nor the steering client has an editing surface for them yet.

## CI validation — Docker system tests (not runnable locally)
- Email slot **plugin-dependency loading** (highest risk — MailKit/MimeKit/BouncyCastle
  copied alongside the provider DLL).
- Core resolves fake code-review slots from inline `ProviderType`+`Settings` bindings.
- Declared-slot set of `pull-request-code-review` matches the six seeded bindings.
- `/api/audit` response shape (camelCase `PagedResult`); Failover timing (8s timeout / 2s
  scan; owner stamped on the status event; re-dispatch queue).

## DevStand
- `ScreenshotHarness` stubbed (targeted the retired dashboard) — rewire to boot
  `Auxilia.AdminConsole` in `EndToEndEnvironment`.

## Core scalability — path to ~100k simultaneous clients
The shape is right (stateless Core.Api, SSE per node, competing-consumer runners, clients
never on the bus); the 2026-08-01 wave delivered shared-queue tracking mirrors, the auth
cache, signed terminal tickets, and the batched audit writer. Remaining:
- **Selective event routing.** `RunStreamPublisher`/`ArtifactStreamPublisher` consume their
  FANOUTs on every node (per-node ingest = global event volume; server-side per-subscriber
  filtering already exists for artifacts). Design for the dedicated session: topic exchanges
  under NEW names (fanout→topic cannot be redeclared in place), routing key = run id
  (status/view) / artifact type (artifacts) stamped at publish (runner + workflow SDK —
  **every workflow image must be rebuilt**), per-node dynamic bindings for runs with open SSE
  streams (subscriptions gain Add/RemoveBinding), tracking mirrors bind `#`, ASB mapping =
  topic subscription rules.
- Infra per the ARCHITECTURE §14.4 table (RabbitMQ cluster / ASB, MongoDB backend). Note:
  100k clients ≠ 100k concurrent runs — the run axis is runner-fleet/container capacity plus
  heartbeat/failover-monitor volume, tracked separately.

## Deeper platform consolidation (from the separation plan)
- Core.Api + Core.Runner shared **Core DB tier** (currently separate DBs; several features
  bridge the split over the bus).
- Endpoint-granular network-policy enforcement + **Run-API quotas** (hardening).
