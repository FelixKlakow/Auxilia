# Auxilia.Workflows.AiAgent

Slot-package that adds AI-agent vocabulary to the workflow SDK. Declares the model requirements a workflow needs (context window, modalities, output tokens). The runtime `IAiAgent` interface is injected after slot resolution by a separate provider package.

Note: `IAiAgent` (slot contract) is intentionally independent of `Auxilia.AI` (the SDK). A provider package bridges them.

## File / Folder Map
```
Source/Auxilia.Workflows.AiAgent/
├── IAiAgent.cs                            # Runtime interface — CreateSession(systemPrompt?) → IAiAgentSession
├── IAiAgentSession.cs                     # Session contract — WithMcpServerTools, ExecuteAsync
├── AiCapabilities.cs                      # ICapability: MinContextWindow, SupportedModalities[], MaxOutputTokens?
├── Modality.cs                            # Enum: Text, Vision, Audio…
└── AiAgentWorkflowBuilderExtensions.cs    # RequiresAiAgent() — thin wrapper over builder.Requires<T>
```