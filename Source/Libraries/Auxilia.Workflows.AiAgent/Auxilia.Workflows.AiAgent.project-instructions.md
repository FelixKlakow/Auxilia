# Auxilia.Workflows.AiAgent

Slot-package that adds AI-agent vocabulary to the workflow SDK. Declares the model requirements a workflow needs (context window, modalities, output tokens). The runtime `IAiAgent` interface is injected after slot resolution by a separate provider package.

Note: `IAiAgent` and `IAiSession` are intentionally independent of `Auxilia.AI` (the SDK). A provider package bridges them.

## Architecture

Named AI slots are registered as keyed services by the provider's `ISlotHandler`:
```csharp
services.AddKeyedSingleton<IAiAgent>(slotName, implementation);
```

Workflow classes inject them with `[FromKeyedServices("slot-name")] IAiAgent agent`.

### Result-Sink Pattern

When an orchestrator needs structured output from the model, it creates a result-sink
`ICapabilityMcpTools`, starts it, registers it in `AiSessionOptions.CapabilityTools`, then reads the
accumulated results after `ExecuteAsync` returns. The model calls typed tools to report results;
the orchestrator **never** parses the `ExecuteAsync` return value for structured data.

Lifecycle shape:
```csharp
new XxxResultSinkMcpTools(...)
    .StartAsync(new HttpMcpTransportConfig("http://localhost:0/mcp", "sink-name"), ct);

var options = new AiSessionOptions { CapabilityTools = [sink, ...otherTools] };
await using var session = await agent.OpenSessionAsync(options, ct);
await session.ExecuteAsync(prompt, ct);   // model calls typed tools

var results = sink.DrainXxx();            // read accumulated results
await sink.StopAsync();                   // always in finally block
```

**Transport:** Always `HttpMcpTransportConfig("http://localhost:0/mcp", ...)`. Port 0 lets the OS
assign a free port; the base class resolves the actual port at start-up and stores it in `CurrentTransport`.

## File / Folder Map
```
Source/Libraries/Auxilia.Workflows.AiAgent/
├── IAiAgent.cs                            # Runtime interface for AI invocation inside a workflow
├── IAiSession.cs                          # Single conversation context; returned by IAiAgent.OpenSessionAsync
├── AiSessionOptions.cs                    # Configuration applied when opening an IAiSession. Key property: CapabilityTools: IReadOnlyList<ICapabilityMcpTools>? — already-started MCP tool servers to register with the session
├── AiCapabilities.cs                      # ICapability: MinContextWindow, SupportedModalities[], MaxOutputTokens?
├── Modality.cs                            # Enum: Text, Vision, Audio…
├── AiAgentWorkflowBuilderExtensions.cs    # RequiresAiAgent() — thin wrapper over builder.Requires<T>
└── Mcp/AiInferenceMcpTools.cs             # MCP tool server that exposes IAiAgent.OpenSessionAsync + IAiSession.ExecuteAsync as a single {slotName}.run_inference tool for the orchestrator AI session
```

## Special rules
- `IAiAgent` and `IAiSession` must not reference `Auxilia.AI`. The SDK bridge lives in provider packages only.
- Never embed JSON-format instructions (e.g. "Respond with JSON:", "Return a JSON array") in AI prompts. Register a result-sink `ICapabilityMcpTools` in `AiSessionOptions.CapabilityTools` instead. See the global copilot-instructions for the full pattern.
- Retry and resilience belong inside the provider's `IAiAgent` implementation, not in a decorator.