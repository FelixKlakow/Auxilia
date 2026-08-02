# Auxilia — Copilot Instructions

Minimal guidance for working in this repo. **Depth lives in the linked docs — read the
relevant one, don't duplicate it here.** This file mirrors `CLAUDE.md`; keep the two aligned.

## Rule 0: Read project instructions first

Every project directory has a `<ProjectName>.project-instructions.md` beside its `.csproj`.
**Read it before touching that project** — purpose, architecture, non-obvious invariants.

## Documentation map (update these; don't create new ones)

- `docs/ARCHITECTURE.md` — full system architecture, the credential/trust model, dispatch
  lifecycle, security.
- `docs/TestStrategy.md` — the test pyramid in detail.
- `docs/CommitConventions.md` — commit format and hooks.
- `docs/backlog.md` — live tracker of known follow-ups.
- `docs/workflow-sdk-design.md`, `docs/view-data-design.md`,
  `docs/implementation-workflow-design.md`, `docs/steering-client-integration.md` —
  live design docs; `docs/delivered/` holds completed programs (context only).

## Stack, layout & commands

.NET 10, ASP.NET Core, Blazor Server, NUnit 4, Moq, Testcontainers. Solution: `Auxilia.slnx`.

`Source/Platform` (deployables: Core.Api, Core.Runner, AdminConsole, TriggerHost) ·
`Source/Libraries` (contracts, clients, workflow SDK, messaging, data, governance) ·
`Source/Slots` (provider plugins) · `Source/Workflows` (shipped workflow images) ·
`Tests/{Platform,Libraries,Slots,Workflows}` (mirroring test projects) · `Tests/System`
(SystemTestSuite, testing utilities, fake slots, DevStand) · `Scripts/` (dev stack & tooling).

```powershell
dotnet build Auxilia.slnx                                              # build everything
dotnet test Auxilia.slnx                                               # full suite (run after a feature/fix)
dotnet test --filter "Category=Unit"                                   # unit (also the pre-commit hook)
dotnet test --filter "Category=Component"                              # component
dotnet test Tests/System/Auxilia.SystemTestSuite/ --filter "Category=System"  # system (Docker required)
```

## Architecture (see `docs/ARCHITECTURE.md`)

Work items from external task sources trigger **signed, stateful workflow programs** that run
in isolated containers and communicate **exclusively via the message bus**
(`IMessageBusClient`). AI agents have full UI parity via an MCP server. Deployables:
**Core.Api** (control plane — REST + MCP, identity/RBAC, Run API, SSE, registry),
**Core.Runner** (execution plane — container launch, egress policy, JIT credentials), and
**AdminConsole** (pure Core client). The workflow domain is a library
(`Auxilia.Workflows.Client`); `Source/Platform/Auxilia.TriggerHost` is the bundled reference
host. Secrets live only in the Core. A workflow is **signature-trusted** and receives scoped
credentials just-in-time, per slot, encrypted for that instance, under a default-deny egress
policy.

## Rules

- **The Core has NO custom/vendor logic.** Core.Api and Core.Runner are semantics-blind
  brokers: no provider-, vendor-, or workflow-specific code. Anything provider-specific lives
  in dynamically registered pieces (slot-handler plugins, provider-catalog descriptors,
  data-driven specs) — or in the workflow itself. If a feature seems to need Core code that
  knows a vendor, invent a registration mechanism instead.
- All RabbitMQ interaction goes through `IMessageBusClient`
  (`Source/Libraries/Auxilia.Messaging`) so tests can inject `FakeMessageBusClient`.
- Retry/resilience logic belongs inside the service implementation — never in a decorator or
  caller-side retry loop.
- AI: never instruct the model to emit structured text ("Respond with JSON"). Collect
  structured output via typed tool calls on a result-sink `ICapabilityMcpTools` in
  `AiSessionOptions.CapabilityTools`.
- XML doc comments (`///`) only when purpose isn't obvious from name and signature; one
  sentence.
- Use Mermaid for all diagrams in markdown.
- **Documentation hygiene:** don't create new markdown docs, READMEs, or changelogs. Update
  the existing doc in the map above.

## Test pyramid (see `docs/TestStrategy.md`)

**Unit** (single class, Moq, no I/O) / **Component** (real DI, fake infra, no network) /
**System** (full Docker via Testcontainers; only cost-generating third-party calls stubbed) /
**Manual** (real external services, pre-release only). Add tests at all applicable levels.

## Commits (see `docs/CommitConventions.md`)

Conventional Commits: `<type>(scope)!: <description>`, types
`feat fix refactor plan docs style merge revert`. A `commit-msg` hook appends
`Refs: #<ticket>` from the branch name — never add it manually. The pre-commit hook runs
unit tests.
