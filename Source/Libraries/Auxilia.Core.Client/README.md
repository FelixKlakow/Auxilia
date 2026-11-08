# Auxilia.Core.Client

Typed HTTP client for the entire [Auxilia](https://github.com/FelixKlakow/Auxilia) Core
API: dispatch and observe runs, manage configurations and connectors, consume artifacts
and platform events, administer identity, read the audit trail and verify its integrity
(`VerifyAuditAsync`). This is the supported way for any application —
service, desktop app, or tool — to drive the Core.

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
    // Typed frames: connection state, run status, view items — reconnect and
    // resume happen inside the client.
    if (frame is StreamEventFrame<RunStreamEvent> { Event: var evt })
        keptPosition = evt.Cursor; // resume later: core.StreamRunAsync(runId, after: keptPosition)
}
```

A run can take files and require tools: upload each file with `UploadRunInputFileAsync` and name
it in `RunRequest.InputFiles` under the input-file name the workflow declares (it reaches that run's
container and nothing else, and is deleted when the run ends); `RunRequest.RequiredTools`
(`name` or `name@version`) makes a run whose image provides another version end `PreFlightFailed`
instead of running.

What a workflow saves of itself while it runs comes back as the run's checkpoints — artifacts
with `Kind == ArtifactKinds.Checkpoint` (a folder as a `.tar.gz`, `MediaType` `application/gzip`),
the last save kept however the run ended. Download one with `OpenArtifactContentAsync`, then
delete it with `DeleteArtifactAsync` (the run's own principals and administrators may; it is
otherwise kept until `ExpiresUtc`). `RunRequest.CheckpointLifetimeHours` asks for their lifetime
where the platform lets runs set one.

Run streams resume: every event carries its position (`RunStreamEvent.Cursor`, also the SSE
`id`), the client reconnects from the last one it yielded, and `StreamRunAsync(runId, after:)`
starts from one you kept — every event exactly once, in order. Without a position a stream
starts at the run's beginning (its current state, every stored view item, then the live ones);
it completes with the run's final status, which carries the workflow's `Reason`. Streams are
resilient by design (automatic reconnect with backoff, idle detection); unary calls run under a
per-call timeout that surfaces as `TimeoutException`, never as your own cancellation. Every Core
surface is on `ICoreClient`, so test doubles are one interface away.

Runs are private to their principals: a run is reachable — status, list, stream, views,
inputs, terminal, its artifacts and events — only by the principal it runs for, the
principal that started it on that one's behalf (`RunRequest.RequestedBy`, needs
`run.on-behalf-of`), the subjects it is shared with, and administrators; anyone else gets
404. Share a run with `SetRunGrantsAsync(runId, new SetRunGrants([...]))` (principals,
groups or directory groups; an empty list makes it private again). Docker workflow images
are registered by digest (`docker://image[:tag]@sha256:…`, see `DockerPackageCoordinate`).

## Package family

| Package | Purpose |
| --- | --- |
| `Auxilia.Core.Contracts` | The wire contracts |
| `Auxilia.Core.Client` | Typed HTTP client for the full Core API (this package) |
| `Auxilia.Workflows.Client` | Workflow-domain library: authoring, triggers, artifact chaining |
| `Auxilia.Workflows` | Workflow SDK for the programs themselves |
| `Auxilia.Messaging` | Message-bus abstraction + RabbitMQ implementation |
| `Auxilia.AI` | AI session abstraction for agentic workflow steps |
| `Auxilia.Steering.Codec` | Dependency-free steering wire protocol |

## License

Business Source License 1.1 — free for personal production use; commercial use requires a
license. See the packaged LICENSE file and the repository for terms.
