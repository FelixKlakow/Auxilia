# Auxilia.Workflows.AiAgent

Slot-package that adds AI-agent vocabulary to the workflow SDK. Declares the model requirements a workflow needs (context window, modalities, output tokens). The runtime `IAiAgent` interface is injected after slot resolution by a separate provider package.

Note: `IAiAgent` and `IAiSession` are intentionally independent of `Auxilia.AI` (the SDK). A provider package bridges them.

## Architecture

Named AI slots are registered as keyed services by the provider's `ISlotHandler`:
```csharp
services.AddKeyedSingleton<IAiAgent>(slotName, implementation);
```

Workflow classes inject them with `[FromKeyedServices("slot-name")] IAiAgent agent`.

## File / Folder Map
```
Source/Auxilia.Workflows.AiAgent/
├── IAiAgent.cs                            # Runtime interface for AI invocation inside a workflow
├── IAiSession.cs                          # Single conversation context; returned by IAiAgent.OpenSessionAsync
├── AiCapabilities.cs                      # ICapability: MinContextWindow, SupportedModalities[], MaxOutputTokens?
├── Modality.cs                            # Enum: Text, Vision, Audio…
└── AiAgentWorkflowBuilderExtensions.cs    # RequiresAiAgent() — thin wrapper over builder.Requires<T>
```

## Special rules
- `IAiAgent` and `IAiSession` must not reference `Auxilia.AI`. The SDK bridge lives in provider packages only.