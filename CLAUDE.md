# CLAUDE.md

Minimal guidance for working in this repo. **Depth lives in the linked docs — read the relevant one, don't duplicate it here.**

## Rule 0: Read project instructions first

Every project directory has a `<ProjectName>.project-instructions.md` beside its `.csproj`. **Read it before touching that project** — purpose, architecture, non-obvious invariants.

## Documentation map (update these; don't create new ones)

**Live design & reference:**
- **`docs/ARCHITECTURE.md`** — full system architecture, the credential/trust model, dispatch lifecycle, security.
- **`docs/TestStrategy.md`** — the test pyramid in detail.
- **`docs/CommitConventions.md`** — commit format and hooks.
- **`docs/steering-client-integration.md`** — human-steering of AI workflows; the steering client as a pure Core client (current source of truth).
- **`docs/backend-service-retirement-plan.md`** — completed program: BackendService dissolved into Core.Api / Studio, its UI rehomed as `Auxilia.AdminConsole`.
- **`docs/backlog.md`** — live tracker of known follow-ups (Core client-surface gaps, config-store move, the full steer loop, CI validation).
- **`docs/workflow-sdk-design.md`** — the workflow SDK / builder contract.
- **`docs/view-data-design.md`** — live views and status fan-out.

**Delivered / historical (`docs/delivered/` — context only, not live design):** core-platform-separation-plan, security-consolidation-plan, governance-rbac-design, enterprise-login-design, goal-v1, workflow-dispatch-test-strategy, human-steering-design (superseded by the steering doc above), and STATS.

## Stack & commands

.NET 10, ASP.NET Core, Blazor Server, NUnit 4, Moq, Testcontainers. Solution: `Auxilia.slnx`.

```powershell
dotnet build Auxilia.slnx                                              # build everything
dotnet test Auxilia.slnx                                               # full suite (run after a feature/fix)
dotnet test --filter "Category=Unit"                                   # unit (also the pre-commit hook)
dotnet test --filter "Category=Component"                              # component
dotnet test Auxilia.SystemTestSuite/ --filter "Category=System"        # system (Docker required)
dotnet test <Project>.Tests/ --filter "FullyQualifiedName~<TestName>"  # single test
```

## Architecture (see `docs/ARCHITECTURE.md`)

Work items from external task sources trigger **signed, stateful workflow programs** that run in isolated containers and communicate **exclusively via the message bus** (`IMessageBusClient`). AI agents have full UI parity via an MCP server. The platform is four deployables sharing `Auxilia.Core.Contracts`/`Auxilia.Core.Client`: **`Auxilia.Core.Api`** (control plane — REST + authenticated MCP, identity/RBAC/groups, connector admin, audit, Run API, live-view SSE, failover monitor), **`Auxilia.Core.Runner`** (execution plane — container launch, egress policy, JIT credential delivery), **`Auxilia.WorkflowStudio`** (workflow product — types/packages, triggers + integration adapters; a pure Core client), and **`Auxilia.AdminConsole`** (operator/admin Blazor UI, a pure Core client with no database). The Core services each own their own database; **secrets live only in the Core**. (The original `Auxilia.BackendService` monolith has been retired — see `docs/backend-service-retirement-plan.md`.)

**Credential/trust model** (details in `docs/ARCHITECTURE.md`): a workflow is **signature-trusted** and receives **scoped** credentials **just-in-time, per slot, encrypted for that instance**, decrypted and used inside the container under a default-deny **egress policy**. The protection is *who gets a credential and when*, not hiding it from the workflow. (Exception: the initial repo clone is done Core-side and its token stripped before the workspace is mounted.)

## Rules

- **The Core has NO custom/vendor logic.** Core.Api and Core.Runner are semantics-blind brokers: no provider-, vendor-, or workflow-specific code (no Anthropic/GitHub/etc. API calls, no special-cased provider types). Anything provider-specific lives in dynamically registered pieces — slot-handler plugins, provider-catalog descriptors, connect flows, data-driven specs like `ProviderOAuthRefresh` — or in the workflow itself. If a feature seems to need Core code that knows a vendor, invent a registration mechanism instead.
- All RabbitMQ interaction goes through `IMessageBusClient` (`Source/Auxilia.Messaging`) so tests can inject `FakeMessageBusClient`.
- Retry/resilience logic belongs inside the service implementation — never in a decorator or caller-side retry loop.
- AI: never instruct the model to emit structured text ("Respond with JSON"). Collect structured output via typed tool calls on a result-sink `ICapabilityMcpTools` in `AiSessionOptions.CapabilityTools`.
- XML doc comments (`///`) only when purpose isn't obvious from name and signature; one sentence.
- Use Mermaid for all diagrams in markdown.
- **Documentation hygiene:** don't create new markdown docs, READMEs, or changelogs. Update the existing doc in the map above and, if a genuinely new topic needs a home, add it to the map. Keep this file minimal — detail belongs in the linked docs.

## Commits (see `docs/CommitConventions.md`)

Conventional Commits: `<type>(scope)!: <description>`, types `feat fix refactor plan docs style merge revert`. A `commit-msg` hook appends `Refs: #<ticket>` from the branch name — never add it manually. The pre-commit hook runs unit tests.
