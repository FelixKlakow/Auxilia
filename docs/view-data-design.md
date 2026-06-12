# Live View Data & Pluggable Dashboards – Implementation Design

> Status: **Approved design** (Task #5)
> Cross-reference: ARCHITECTURE.md §15 (the model), docs/goal-v1.md

One general mechanism carries all workflow→frontend data: **named, schema-declared views**.
A live agent stream, a progress log, a findings table, and a finished run's result page are
the same concept — only the rendering hint and lifecycle differ.

## 1. Declaration (SDK, packaging time)

Workflows declare views in the builder; the schema (embedded in the signed package and sent
in the manifest) carries the descriptors, so the frontend can render any workflow's views
without frontend changes:

```csharp
WorkflowBuilder.Create("pull-request-code-review")
    .DeclaresView<AgentMessage>("agent-conversation", ViewRendering.Stream, ViewLifecycle.LiveAndPersisted)
    .DeclaresView<CodeReviewFinding>("review-findings", ViewRendering.Table, ViewLifecycle.Persisted)
```

`ViewDescriptor(Name, ItemSchemaJson, Rendering, Lifecycle)` joins `WorkflowSchema.Views`
and `WorkflowManifest.Views`. Rendering: `Stream | Log | Table | Chart | Markdown | Custom`.
Lifecycle: `Live | Persisted | LiveAndPersisted`.

## 2. Publication (SDK, run time)

Workflows resolve `IViewPublisher` from DI:

```csharp
await viewPublisher.PublishAsync("agent-conversation", new AgentMessage(...));
```

The SDK wraps each item in a `ViewDataMessage(WorkflowInstanceId, ViewName, Sequence, PayloadJson)`
published to the **`workflow.view-data` exchange**. `Sequence` is a per-(instance, view)
monotonic counter assigned by the SDK so consumers can order and de-duplicate. Payloads are
view items only — large blobs belong in the Artifact Store, referenced from the payload.

## 3. Steering Instance: persistence

`ViewDataHandler` consumes the exchange. For views whose lifecycle includes Persisted
(descriptor known from the registration manifest, cached per instance), each item is stored
as a `ViewDataRecord(InstanceId, ViewName, Sequence, PayloadJson, TimestampUtc)` in the
platform data layer — so finished runs replay through the identical rendering path.
Live-only views are not stored.

## 4. Backend Service: live fan-out

The dashboard backend consumes the same exchange and forwards items to subscribed SignalR
circuits (group per `instanceId:viewName`). Subscription requires a `view.subscribe` policy
check; access to a view follows the workflow type it belongs to (workflow-type access lists).
Replay of a finished run = read `ViewDataRecord`s ordered by Sequence and push them through
the same client-side renderer.

## 5. Frontend rendering (descriptor-driven)

One `ViewRenderer` component takes `(ViewDescriptor, IAsyncEnumerable<item>)` and dispatches
on the rendering hint: Stream → append-only message list; Log → monospaced lines; Table →
columns derived from the item schema; Chart → series mapping by convention; Markdown →
rendered document; Custom → plugin component registry (later). Dashboards compose renderers:
per-run dashboards materialize automatically from the run's schema; operators pin views from
multiple workflows into shared dashboards (`DashboardRecord` with view references).

## 6. Backpressure & limits

- The SDK publisher is fire-and-forget onto the bus; no per-item ack.
- The persistence handler enforces an operator-configurable per-view item cap
  (`ViewDataSettings.MaxItemsPerView`, default 10 000) — beyond it, oldest items are dropped
  for `Log`/`Stream` views and writes are rejected (audited) for others.
- SignalR fan-out drops items for slow circuits older than the latest N (client re-syncs via
  replay); live views are eventually consistent with the persisted record, never ahead of it.

## 7. MCP parity

The MCP server exposes `get_view_data(instanceId, viewName, fromSequence)` and a streaming
subscription backed by the same store/exchange — no hidden data channel.
