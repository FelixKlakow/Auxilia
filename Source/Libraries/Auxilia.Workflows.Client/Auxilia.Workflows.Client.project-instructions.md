# Auxilia.Workflows.Client

The **workflow-domain convenience library** on top of the raw `Auxilia.Core.Client` — the
successor of the retired `Auxilia.WorkflowStudio` deployable. Adopters host it wherever they
like: as hosted services in a server process, or driven manually inside a desktop app.
Triggers fire only while some host embedding this library runs.

## Invariants
- **Pure Core client.** Talks to the Core exclusively through `ICoreClient` — never the
  message bus, never a Core database. Artifact chaining consumes the Core's
  **server-side-filtered artifact SSE stream** (`StreamArtifactEventsAsync`), one consumer per
  distinct artifact type — the library must never receive the global artifact feed. Event
  triggers ride the analogous **filtered event SSE stream** (`StreamEventsAsync`) under the
  same one-consumer-per-type rule; the `run.*` types are the platform's reserved run-lifecycle
  vocabulary.
- **The Core registry is the only workflow-type catalog.** `WorkflowAuthoring` validates
  against `GetWorkflowSchemaAsync`; there is no library-side type store (the Studio's private
  catalog died with it). Client-side validation is the fast path — the Core re-validates on
  submit and stays the authority.
- **Trigger storage is the host's concern** (`ITriggerStore`; in-memory default). Trigger
  definitions are not Core state — what-follows-what is workflow-domain policy.
- **Every dispatch goes through the Core Run API** on behalf of the trigger's run-as
  principal, passing the same policy checks as a manual run. One failing trigger is logged
  and skipped; engines never die from a single bad dispatch.
- **Stream reconnect lives in `Auxilia.Core.Client`**, not here — the engines consume the
  resilient frame stream and add what only they can: on every reconnect they CATCH UP via
  the matching query (`QueryArtifactsAsync`/`QueryEventsAsync` with `CreatedAfterUtc:
  lastSeen`; gap events are not replayed by the stream) and dedupe catch-up/live overlap by
  id (bounded memory). Adding a trigger for a NEW artifact/event type needs `RefreshAsync` to
  open its filtered stream; edits to existing triggers apply per event without a restart.
- **ITriggerStore is a breaking surface by design:** a new trigger kind adds per-kind members
  (get/save/delete); external store implementations break loudly at compile time instead of a
  feature silently no-opping.

## Integrating
```csharp
services.AddCoreClient("https://core.internal:8443", apiKey);
services.AddWorkflowClient();          // authoring + engines (+ in-memory trigger store)
services.AddWorkflowClientHosting();   // server host: engines run as hosted services
// Desktop host instead: inject the engines, call StartAsync/StopAsync (or TickAsync) yourself.
```
Email work-item intake stays in `Auxilia.Adapters.Email` (its own package, already
Core-client-dispatching); register it alongside when the host wants mailbox triggers.

## File / Folder Map
```
Source/Libraries/Auxilia.Workflows.Client/
├── WorkflowClientExtensions.cs     # AddWorkflowClient / AddWorkflowClientHosting + WorkflowClientOptions
├── Authoring/
│   └── WorkflowAuthoring.cs        # Schema-validated configure (+ ConfigureAndRunAsync)
└── Triggers/
    ├── TriggerDefinitions.cs       # Scheduled- / Artifact- / EventTriggerDefinition
    ├── ITriggerStore.cs            # Host-pluggable persistence + InMemoryTriggerStore
    ├── ScheduledTriggerEngine.cs   # Interval sweep (hosted or manual TickAsync)
    ├── ArtifactChainingEngine.cs   # Filtered artifact-SSE consumers -> follow-up dispatches
    └── EventTriggerEngine.cs       # Filtered event-SSE consumers -> follow-up dispatches
```
