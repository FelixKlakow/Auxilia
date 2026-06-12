# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Rule 0: Read project instructions first

Every project directory contains a `<ProjectName>.project-instructions.md` co-located with its `.csproj`. **Read it before touching any file in that project.** It covers the project's purpose, architecture, and non-obvious invariants.

## Stack & Commands

.NET 10, ASP.NET Core, Blazor Server, NUnit 4, Moq, Testcontainers. Solution file: `Auxilia.slnx`.

```powershell
dotnet build Auxilia.slnx                                  # build everything
dotnet test Auxilia.slnx                                   # full test suite (run after finishing a feature/fix)
dotnet test --filter "Category=Unit"                       # unit tests (also run by pre-commit hook)
dotnet test --filter "Category=Component"                  # component tests
dotnet test Auxilia.SystemTestSuite/ --filter "Category=System"   # system tests (Docker must be running)
dotnet test <Project>.Tests/ --filter "FullyQualifiedName~<TestName>"   # single test
```

## Test pyramid (see TestStrategy.md)

When implementing a feature, add tests at all applicable levels. Every fixture carries an NUnit `[Category(...)]` attribute.

1. **Unit** (`*.Tests/UnitTests/`, `Category=Unit`) — single class, all deps mocked with Moq (prefer `MockBehavior.Strict`), no I/O. Runs on every commit via the pre-commit hook.
2. **Component** (`*.Tests/ComponentTests/`, `Category=Component`) — real DI container via `IHost`/`WebApplicationFactory`, infra replaced with in-memory fakes (`FakeMessageBusClient`), no network.
3. **System** (`Auxilia.SystemTestSuite/`, `Category=System`) — real infrastructure in Docker via Testcontainers; only cost-generating third-party calls (Azure DevOps, AI) are stubbed. Each environment is a namespace-scoped `[SetUpFixture]` under `Environments/`; test classes in the matching namespace under `SystemTests/` share it automatically. Images are rebuilt from local source before containers start.
4. **Manual** — real external services, pre-release only.

## Architecture (see ARCHITECTURE.md for full detail)

Auxilia is a workflow-driven distributed system: work items from external task sources (Jira, ADO, Trello, GitHub) trigger **signed, stateful workflow programs** that run in isolation and communicate **exclusively via the message bus**. AI agents are first-class citizens with full UI parity through an MCP server.

Key flow: Integration Adapters pull work items → Orchestration runs pre-flight (signature, account bundles, resources) → **Steering Instance** (the mediator between bus, frontend, and AI agents) launches the workflow via `IWorkflowRunner` → workflow reaches external systems only through the audited **Resource Proxy** and declarative **Network Egress Layer** (default-deny allowlist) → outputs are pushed back by the **Workspace Manager** using scoped credentials — workflows never hold raw credentials.

Project layout:
- `Source/` — platform services: `Auxilia.BackendService`, `Auxilia.SteeringInstance`, `Auxilia.Messaging` (the `IMessageBusClient` abstraction), workflow capability libraries (`Auxilia.Workflows.SourceControl`, `.TaskSource`, `.AiAgent`, `.PullRequestAccess`, `.TestRunner`), and concrete workflows (`Auxilia.CodeReview.Workflow`, `Auxilia.ImplementationWorkflow`).
- Repo root — the workflow SDK (`Auxilia.Workflows`), packaging (`Auxilia.Workflows.Packer`), AI integration (`Auxilia.AI`), data access (`Auxilia.UniversalDataAccess`), test projects, and `Auxilia.FakeSlots.*` (fake slot plugin DLLs used by workflow tests).

## Rules

- All RabbitMQ interaction goes through `IMessageBusClient` (`Source/Auxilia.Messaging`) so tests can inject `FakeMessageBusClient`.
- Retry/resilience logic belongs inside the service implementation — never in a decorator or caller-side retry loop.
- AI: never instruct the model to emit structured text ("Respond with JSON"). Use a result-sink `ICapabilityMcpTools` in `AiSessionOptions.CapabilityTools` to collect structured output via typed tool calls.
- XML doc comments (`///`) only when purpose isn't obvious from name and signature; keep them to one sentence.
- Use Mermaid for all diagrams in markdown documents.
- Don't generate documentation files, README updates, or changelogs unless explicitly asked.

## Commits (see CommitConventions.md)

Conventional Commits: `<type>(optional scope)!: <description>` with types `feat fix refactor plan docs style merge revert`. A `commit-msg` hook auto-appends `Refs: #<ticket>` from branch names containing a 4+-digit ticket number — never add it manually. The pre-commit hook runs unit tests.
