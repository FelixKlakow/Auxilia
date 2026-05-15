# Auxilia – Copilot Instructions

## Stack
.NET 10 ASP.NET services, NUnit 4, Moq, Testcontainers. Solution: `Auxilia.slnx`.

## Test Pyramid
~~Three levels of automated tests exist. **Add all three when implementing a feature. For bug fixes, add a test at the appropriate level if the bug is testable.**

| Level | Project | Filter | When run |
|---|---|---|---|
| 1 Unit | `*.Tests/UnitTests/` | `Category=Unit` | Every commit (pre-commit hook) |
| 2 Component | `*.Tests/ComponentTests/` | `Category=Component` | On demand / CI |
| 3 System | `Auxilia.SystemTestSuite/` | `Category=System` | On demand / CI |~~

- **Unit** – single class, all deps mocked with Moq, no I/O.
- **Component** – real DI container, fake infra (`FakeMessageBusClient`), no network.
- **System** – full Docker environment via Testcontainers; only cost-generating third-party calls are stubbed.

## Completing work
After finishing a feature or bug fix, run the full test suite:
```
dotnet test Auxilia.slnx
```

_## Messaging
Use `IMessageBusClient` (abstraction in `Auxilia.Messaging`) for all RabbitMQ interactions so tests can inject `FakeMessageBusClient`._

## Diagrams
Always use **Mermaid** for diagrams in markdown documents.

## Rule 0: Always Load Project Instructions FIRST

Each project directory contains an entry-point file named `<ProjectName>.project-instructions.md` (matching the assembly/folder name) co-located with its `.csproj`. These files are the **single entry point** when working on a project. Their purpose is to **reduce tool calls and prevent wrong-entrance mistakes** by giving an at-a-glance view of:

- The project's role and responsibility in the solution.
- Allowed/forbidden dependencies and layer boundaries.
- Project-scoped hard rules and architectural constraints.
- A **file/folder map** that points to where things live (folder → purpose), so Copilot can jump straight to the right file instead of searching.

## Commit convention
`<type>: <description>` – allowed types: `feat fix refactor plan docs style merge revert`.

