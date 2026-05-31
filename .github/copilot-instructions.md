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

## AI Session Retry and Resilience
Retry logic and resilience for `IAiAgent.OpenSessionAsync` and `IAiSession.ExecuteAsync` must be
handled at the `IAiAgent` layer — either inside the provider implementation or via a decorator such
as `ResilientAiAgent` from `Auxilia.Workflows.AiAgent`.  
**Never** implement per-workflow retry loops in orchestrators or workflow classes.  
Use `AiAgentServiceCollectionExtensions.WrapAiAgentWithResilience` to opt in to the decorator.

## Structured AI Output: Result-Sink MCP Tool Pattern

**Never** instruct the AI model to "respond with JSON", "return a JSON array", or emit any structured
text format in its reply. When an orchestrator needs structured output, use a result-sink
`ICapabilityMcpTools` registered in `AiSessionOptions.CapabilityTools`.

### The four-step pattern

1. **Instantiate and start** the result-sink MCP tool server (e.g. `CodeReviewResultSinkMcpTools`,
   `ImplementationReviewResultSinkMcpTools`).
2. **Register it** in `AiSessionOptions.CapabilityTools` when calling `IAiAgent.OpenSessionAsync`.
3. **Call `ExecuteAsync`** with a plain prompt — the model calls the typed tools to report results.
4. **Drain results** after `ExecuteAsync` returns (e.g. `DrainFindings()`, `TakeFileVerdict()`,
   `DrainNotes()`). Always **stop the sink in a `finally` block**.

```csharp
var sink = new XxxResultSinkMcpTools(...);
await sink.StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "sink-name"), ct);
try
{
    var options = new AiSessionOptions { CapabilityTools = [sink, ...otherTools] };
    await using var session = await agent.OpenSessionAsync(options, ct);
    await session.ExecuteAsync(prompt, ct);   // model calls typed tools

    var results = sink.DrainXxx();            // read accumulated results
}
finally
{
    await sink.StopAsync();
}
```

### Anti-patterns — never do these

- Prompts containing "Respond with JSON:", "Return a JSON array", or any structured-output instruction.
- `ParseXxx` helper methods that deserialise `ExecuteAsync` return values.
- `catch { return default; }` or `catch (JsonException) { return []; }` as silent fallbacks.

### Key types

| Type | Location |
|---|---|
| `ICapabilityMcpTools` | `Auxilia.Workflows/Mcp/` |
| `AiSessionOptions.CapabilityTools` | `Auxilia.Workflows.AiAgent/` |
| `HttpMcpTransportConfig` | `Auxilia.Workflows/Mcp/` |
| `CapabilityMcpToolsBase` | `Auxilia.Workflows/Mcp/` |

## Commit convention
`<type>: <description>` – allowed types: `feat fix refactor plan docs style merge revert`.

