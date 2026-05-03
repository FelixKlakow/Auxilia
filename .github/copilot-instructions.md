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

## Commit convention
`<type>: <description>` – allowed types: `feat fix refactor plan docs style merge revert`.

