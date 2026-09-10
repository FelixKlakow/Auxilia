# Auxilia.TriggerHost

The **always-on reference host** for the workflow-domain client library
(`Auxilia.Workflows.Client`) plus email work-item intake (`Auxilia.Adapters.Email`). This is
deliberately a THIN composition — everything it hosts is embeddable in any other app; the
host only provides the always-on process, its storage, and `/health`.

## Invariants
- **Pure Core client**: drives the Core exclusively through `ICoreClient` (REST + SSE).
  No message-bus dependency — artifact chaining rides the Core's filtered artifact stream and
  event triggers ride the Core's filtered event stream.
- **Own storage, host-owned credentials.** Mailbox credentials live in THIS host's protected
  records (`SlotInstanceRecord`), not in Core connectors — connector secrets never leave the
  Core by design, so client-side intake credentials are host configuration.
- No workflow-type catalog, no authoring REST surface — the Core registry is the only
  catalog; authoring is a library call (`WorkflowAuthoring`), not an endpoint.
- `public partial class Program;` supports `WebApplicationFactory` component tests.

## Configuration
- `Core:*` — the WHOLE `CoreClientOptions` bound from one section: `BaseAddress` (default
  `http://localhost:8080`), `ApiKey` (service principal key), and the client knobs
  `StreamReconnectInitialBackoffSeconds` / `StreamReconnectMaxBackoffSeconds` /
  `StreamIdleTimeoutSeconds` / `UnaryTimeoutSeconds`.
- `PlatformData:*` — this host's storage backend.
- `MailboxTriggers:TickSeconds` — email intake sweep.
- `WorkflowClient:SchedulerIntervalSeconds` — scheduler pacing (`WorkflowClientOptions`).
- `Tests/Platform/Auxilia.TriggerHost.Tests` boots the host through `WebApplicationFactory`
  and pins the `Core:*` binding.
