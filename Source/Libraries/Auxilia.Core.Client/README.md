# Auxilia.Core.Client

Typed HTTP client for the entire [Auxilia](https://github.com/FelixKlakow/Auxilia) Core
API: dispatch and observe runs, manage configurations and connectors, consume artifacts,
administer identity. This is the supported way for any application — service, desktop app,
or tool — to drive the Core.

Auxilia is a self-hosted platform for governed AI agent workflows: signed workflow
containers, just-in-time scoped credentials, live operator steering, and full REST + MCP
parity.

## Getting started

```csharp
using Auxilia.Core.Client;
using Auxilia.Core.Contracts;

var http = new HttpClient { BaseAddress = new Uri("https://your-core-host") };
http.DefaultRequestHeaders.Authorization = new("Bearer", apiKeyOrUserBearer);
ICoreClient core = new CoreClient(http);

// Dispatch a run of a registered workflow type and follow it live.
var accepted = await core.RunAsync(new RunRequest("my-workflow-type"));
await foreach (var frame in core.StreamRunAsync(accepted.RunId))
{
    // Typed frames: connection state, run status, live views — reconnect,
    // backoff, and dedupe happen inside the client.
}
```

Streams are resilient by design (automatic reconnect with backoff, idle detection,
snapshot-first frames); unary calls run under a per-call timeout. Every Core surface is on
`ICoreClient`, so test doubles are one interface away.

## Package family

| Package | Purpose |
| --- | --- |
| `Auxilia.Core.Contracts` | The wire contracts |
| `Auxilia.Core.Client` | Typed HTTP client for the full Core API (this package) |
| `Auxilia.Workflows.Client` | Workflow-domain library: authoring, triggers, artifact chaining |
| `Auxilia.Steering.Codec` | Dependency-free steering wire protocol |

## License

Business Source License 1.1 — free for personal production use; commercial use requires a
license. See the packaged LICENSE file and the repository for terms.
