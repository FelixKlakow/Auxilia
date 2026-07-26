# Auxilia – Copilot Instructions

## Stack
.NET 10 ASP.NET services, NUnit 4, Moq, Testcontainers. Solution: `Auxilia.slnx`.

## Rule 0: Always Load Project Instructions FIRST

Each project directory contains `<ProjectName>.project-instructions.md` co-located with its `.csproj`.  
**Read it before touching any file in that project.**

Each file contains:
- **Purpose** – what the project is for (a few lines max).
- **Architecture** – key decisions and patterns that are not obvious from reading file names. Mermaid diagrams where they help.
- **File/folder map** – folder → what lives there. Descriptions of intent, no implementation detail.
- **Special rules** *(optional)* – only non-obvious invariants specific to this project.

Do **not** include: configuration samples, test category rules, dependency tables, or anything already covered here.

## General Rules

- Use **Mermaid** for all diagrams in markdown documents.
- Retry and resilience belong inside the service implementation. Never use a decorator or caller-side retry loop unless the exception is justified.
- XML doc comments (`///`) are required only when the purpose or usage of a type or member is not obvious from its name and signature. Keep them short and precise — one sentence is usually enough.
- Do not generate documentation files, README updates, or changelogs unless explicitly asked.

## Test Pyramid

Four tiers, as defined in `docs/TestStrategy.md` — **Unit / Component / System / Manual**. Add tests at all applicable levels when implementing a feature; for bug fixes, add a test at the appropriate level if the bug is testable.

- **Unit** – single class, all deps mocked with Moq, no I/O.
- **Component** – real DI container, fake infra (`FakeMessageBusClient`), no network.
- **System** – full Docker environment via Testcontainers; only cost-generating third-party calls are stubbed.
- **Manual** – real external services, pre-release only.

## Completing work
After finishing a feature or bug fix, run the full test suite:
```
dotnet test Auxilia.slnx
```

## Messaging
Use `IMessageBusClient` (abstraction in `Auxilia.Messaging`) for all RabbitMQ interactions so tests can inject `FakeMessageBusClient`.

## AI

- Never instruct the AI model to emit structured text (e.g. "Respond with JSON"). Use a result-sink `ICapabilityMcpTools` in `AiSessionOptions.CapabilityTools` to collect structured output via typed tool calls.

## Commit convention
`<type>(optional scope)!: <description>` – allowed types: `feat fix refactor plan docs style merge revert`.

