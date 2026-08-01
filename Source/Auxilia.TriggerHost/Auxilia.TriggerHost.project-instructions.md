# Auxilia.TriggerHost

The **always-on reference host** for the workflow-domain client library
(`Auxilia.Workflows.Client`) plus email work-item intake (`Auxilia.Adapters.Email`). This is
deliberately a THIN composition — everything it hosts is embeddable in any other app; the
host only provides the always-on process, its storage, and `/health`.

## Invariants
- **Pure Core client**: drives the Core exclusively through `ICoreClient` (REST + SSE).
  No message-bus dependency — artifact chaining rides the Core's filtered artifact stream.
- **Own storage, host-owned credentials.** Mailbox credentials live in THIS host's protected
  records (`SlotInstanceRecord`), not in Core connectors — connector secrets never leave the
  Core by design, so client-side intake credentials are host configuration.
- No workflow-type catalog, no authoring REST surface — the Core registry is the only
  catalog; authoring is a library call (`WorkflowAuthoring`), not an endpoint.
- `public partial class Program;` supports `WebApplicationFactory` component tests.

## Configuration
- `Core:BaseAddress`, `Core:ApiKey` — the Core to drive (service principal key).
- `PlatformData:*` — this host's storage backend.
- `MailboxTriggers:TickSeconds` — email intake sweep.
- `WorkflowClient:*` — engine pacing (`SchedulerIntervalSeconds`, `StreamReconnectMaxBackoffSeconds`).
