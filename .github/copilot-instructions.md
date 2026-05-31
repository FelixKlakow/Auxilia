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

Each project directory contains `<ProjectName>.project-instructions.md` co-located with its `.csproj`.  
**Read it before touching any file in that project.**

Each file contains:
- **Purpose** – what the project is for (a few lines max).
- **Architecture** – key decisions and patterns that are not obvious from reading file names. Mermaid diagrams where they help.
- **File/folder map** – folder → what lives there. Descriptions of intent, no implementation detail.
- **Special rules** *(optional)* – only non-obvious invariants specific to this project.

Do **not** include: configuration samples, test category rules, dependency tables, or anything already covered here.

## AI

- Retry and resilience must be implemented directly inside the `IAiAgent` provider implementation. Never use a decorator or per-workflow retry loops.
- Never instruct the AI model to emit structured text (e.g. "Respond with JSON"). Use a result-sink `ICapabilityMcpTools` in `AiSessionOptions.CapabilityTools` to collect structured output via typed tool calls.

## Commit convention
`<type>: <description>` – allowed types: `feat fix refactor plan docs style merge revert`.

