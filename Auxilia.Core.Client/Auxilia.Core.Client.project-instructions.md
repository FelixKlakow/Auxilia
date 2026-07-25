# Auxilia.Core.Client

Typed HTTP client for the `Auxilia.Core.Api` REST surface. The only supported way for another service (e.g. `Auxilia.WorkflowStudio`) to drive the Core — nothing reaches the Core's database or bus directly.

## Invariants
- Talks to the Core over REST only, authenticating with the configured API key as a bearer token. No message-bus dependency.
- Speaks in `Auxilia.Core.Contracts` DTOs — it shares the wire contract with the server, so the two cannot drift.
- `AddCoreClient(baseAddress, apiKey)` registers `ICoreClient` (a typed `HttpClient`) in DI.

## File / Folder Map
```
Auxilia.Core.Client/
├── ICoreClient.cs           # The client abstraction (runs, configurations, connectors)
├── CoreClient.cs            # HttpClient implementation over the Core REST endpoints
└── CoreClientExtensions.cs  # AddCoreClient(baseAddress, apiKey) DI registration
```
