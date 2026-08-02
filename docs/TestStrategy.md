# Test Strategy

## Overview

Auxilia uses a four-tier test pyramid. Each tier has a distinct scope, dependency profile, and execution cadence.

```
▲  Manual (Level 4)
— real endpoints, real AI credits, run before releases
▲  System Tests (Level 3)
— full Docker environment, run on-demand / dedicated CI stage
▲  Component Tests (Level 2)
— simulated host startup, in-memory fakes, run on-demand / CI
▲  Unit Tests (Level 1)
— pure logic, no I/O, run on every commit (pre-commit hook)
```

---

## Level 1 – Unit Tests

**Projects:** `Tests/Platform/Auxilia.Core.Api.Tests/UnitTests/`, `Tests/Platform/Auxilia.Core.Runner.Tests/`, `Tests/Libraries/Auxilia.Workflows.Client.Tests/UnitTests/`, `Tests/Platform/Auxilia.AdminConsole.Tests/`

**Scope:** A single class or function in isolation. All external dependencies are replaced with Moq mocks.

**Rules:**

- No file I/O, no network, no containers.
- Each test must complete in milliseconds.
- Mocks are strict (`MockBehavior.Strict`) where practical to catch unexpected calls.
- NUnit `[Category("Unit")]` attribute on every fixture.

**Execution:**

- Automatically on every `git commit` via the `pre-commit` hook.
- Run manually: `dotnet test --filter "Category=Unit"`

---

## Level 2 – Component Tests

**Projects:** `Tests/Platform/Auxilia.Core.Api.Tests/ComponentTests/` (incl. the multi-client SSE concurrency fixtures), `Tests/Platform/Auxilia.AdminConsole.Tests/` (bUnit)

**Scope:** Most of a single service executable is constructed using the real DI container, but external infrastructure (
RabbitMQ, databases, HTTP endpoints) is replaced with in-memory fakes (`FakeMessageBusClient`, etc.).

**Rules:**

- No real network connections.
- May use `IHost` / `WebApplicationFactory` to build the full service pipeline.
- Tests are time-bounded (max 10 s per assertion) using `WaitForConditionAsync`.
- NUnit `[Category("Component")]` attribute on every fixture.

**Execution:**

- On-demand or in a dedicated CI stage (after unit tests pass).
- Run manually: `dotnet test --filter "Category=Component"`

---

## Level 3 – System Tests

**Projects:** `Tests/System/Auxilia.SystemTestSuite/`

**Scope:** The entire system or a meaningful sub-system is started with real infrastructure running in Docker
containers (via Testcontainers). Only deliberate cost-generating third-party integrations (Azure DevOps, AI services)
are replaced with in-process abstractions / stubs.

**Structure:**

- Each environment is a `[SetUpFixture]` class in its own namespace under `Environments/`. The fixture starts all
  required containers, exposes shared clients, and tears everything down in `[OneTimeTearDown]`.
- Test classes in the corresponding namespace under `SystemTests/` share that environment automatically via NUnit's
  namespace-scoped `[SetUpFixture]`.
- Running a single environment's tests: `dotnet test --filter "namespace=Auxilia.SystemTestSuite.SystemTests"`
- Running all system tests: `dotnet test Tests/System/Auxilia.SystemTestSuite/`

**Current environments:**

| Environment                   | Containers                                                        | Test class(es)                                                                  |
|-------------------------------|------------------------------------------------------------------|---------------------------------------------------------------------------------|
| `MongoDbEnvironment`          | MongoDB                                                           | `MongoDbSystemTests`                                                             |
| `WorkflowDispatchEnvironment` | RabbitMQ + Core.Runner                                            | `WorkflowDispatchSystemTests`, `MessageBusFanoutSystemTests`                     |
| `CoreApiDispatchEnvironment`  | RabbitMQ + Core.Api + Core.Runner (+ git server)                 | `CoreApiDispatchSystemTests`, `CredentialResolutionSystemTests`, `RepositoryWorkspaceSystemTests` |
| `CodeReviewWorkflowEnvironment`   | RabbitMQ + Core.Runner + baked code-review image             | `CodeReviewWorkflowSystemTests`                                                  |
| `ImplementationWorkflowEnvironment` | RabbitMQ + Core.Runner + baked implementation image        | `ImplementationWorkflowSystemTests`                                             |
| `EmailAdapterEnvironment`     | GreenMail                                                        | `EmailAdapterSystemTests`                                                        |
| `IdentityImportEnvironment`   | OpenLDAP + MongoDB                                               | `IdentityImportSystemTests`                                                      |
| `FailoverEnvironment`         | RabbitMQ + Mongo + **Core.Api** (failover monitor) + two Core.Runners | `FailoverSystemTests`                                                       |
| `EndToEndEnvironment`         | GreenMail + RabbitMQ + Mongo + Core.Runner + **Core.Api** + **TriggerHost** | `EndToEndSystemTests`, `ClaudeCodeWorkflowSystemTests`                 |

> **BackendService retirement (Phase 4):** the BackendService-only environments (`SingleBackendService`,
> `DualBackend`) were removed — they tested BackendService-local plumbing (queue declaration, identification
> round-trip, dual-instance routing, OTEL telemetry) that no longer exists. `Failover` was retargeted onto
> the Core.Api bus-based failover monitor; `EndToEnd` was retargeted so the mail path runs on the
> TriggerHost (email adapter → Core Run API on-behalf-of; ex-WorkflowStudio, retired 2026-08-01)
> with a Core.Api control plane instead of the BackendService host.

**Rules:**

- Docker must be running on the host.
- The latest image is always built from the local source before containers are started (no stale images).
- Tests are time-bounded (20–30 s per assertion).
- NUnit `[Category("System")]` attribute on every fixture.

**Execution:**

- On-demand or in a dedicated resource-intensive CI stage.
- Run manually: `dotnet test Tests/System/Auxilia.SystemTestSuite/ --filter "Category=System"`

---

## Level 4 – Manual Tests

**Scope:** Full end-to-end flows with real external services (live AI credits, real Azure DevOps boards, production-like
data). These produce real costs and side-effects.

**Execution:** Performed manually by a developer or QA engineer before each release. Not automated.

---

## Tooling Summary

| Tool                    | Purpose                                         |
|-------------------------|-------------------------------------------------|
| NUnit 4                 | Test framework (all tiers)                      |
| Moq                     | Mock framework (unit tests)                     |
| `FakeMessageBusClient`  | In-memory `IMessageBusClient` (component tests) |
| Testcontainers.RabbitMq | Real RabbitMQ in Docker (system tests)          |
| `pre-commit` Git hook   | Runs unit tests before every commit             |

---

## Adding a New System Test Environment

1. Create `Tests/System/Auxilia.SystemTestSuite/Environments/<Name>Environment.cs` as a `[SetUpFixture]`.
2. Define the namespace (e.g. `Auxilia.SystemTestSuite.SystemTests.<Name>`).
3. Create `Tests/System/Auxilia.SystemTestSuite/SystemTests/<Name>SystemTests.cs` in the same namespace.
4. The environment starts and stops automatically when tests in that namespace run.

