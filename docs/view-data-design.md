# Live View Data & Pluggable Dashboards – Implementation Design

> Status: **Delivered / current** (Task #5)
> Cross-reference: docs/ARCHITECTURE.md §15 (the model), docs/delivered/goal-v1.md

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

### Declared view data — presentation content before any run

A view may carry **packaging-time data**: `ViewDescriptor.DeclaredDataJson`, opaque JSON
whose meaning belongs to the view's renderer (named by `RendererKey`). The platform only
transports it — no schema field per presentation concept, no Core semantics.

The first user is the **step flow**: the `"flow"` view (renderer key `step-flow`) declares
its steps as data —

```csharp
.DeclaresView<WorkflowStepFlow>(WorkflowStepFlow.ViewName,
    ViewRendering.Custom, ViewLifecycle.LiveAndPersisted,
    WorkflowStepFlow.RendererKey,
    declaredData: new[] {
        new FlowStepDescriptor("plan", "Plan", "Draft the implementation plan.",
            Inputs: ["user-plan-gate", "ai-plan-review"]),
        new FlowStepDescriptor("push", "Push", "Commit and push.",
            SkipInput: "push-mode", SkipValue: "skip", Inputs: ["push-mode"]) })
```

`FlowStepDescriptor(Id, Label, Description, SkipInput, SkipValue, Inputs)`: config and
dispatch UIs render the pipeline's shape **before any run exists** — the stage view in the
configuration panel. `SkipInput`/`SkipValue` is a data-driven hint: the step presents as
skipped when the effective value of that run input equals the value (live, as the operator
toggles inputs). `Inputs` names the run inputs belonging to the step — a skipped step's
exclusive inputs drop out of the form (a step's gating input always stays visible so it can
be re-enabled). The platform never learns what any of it means. The runtime `"flow"` view
then carries only **state snapshots** (`WorkflowStepFlow` = `WorkflowStepState(Id, State)`,
open state vocabulary: pending/active/done/skipped) — observers join states onto the
declared steps by id. Labels and descriptions live in exactly one place: the declaration.

## 2. Publication (SDK, run time)

Workflows resolve `IViewPublisher` from DI:

```csharp
await viewPublisher.PublishAsync("agent-conversation", new AgentMessage(...));
```

The SDK wraps each item in a `ViewDataMessage(WorkflowInstanceId, ViewName, Sequence, PayloadJson)`
published to the **`workflow.view-data` exchange**. `Sequence` is a per-(instance, view)
monotonic counter assigned by the SDK so consumers can order and de-duplicate. Payloads are
view items only — large blobs belong in the Artifact Store, referenced from the payload.

## 3. Persistence (Core.Api)

Core.Api mirrors the exchange (`RunViewTrackingService`): each item is stored as a
`CoreRunViewRecord(RunId, ViewName, Sequence, PayloadJson, TimestampUtc)` in the **Core.Api
database** (capped per run — large data belongs in the Artifact Store) and read back over
`GET /api/runs/{id}/views` (`ICoreClient.GetRunViewsAsync`) — so finished runs replay through
the identical rendering path.

## 4. Live fan-out (Core.Api SSE)

Core.Api's `RunStreamPublisher` consumes the same exchange and fans each item to the run's
open `GET /api/runs/{id}/stream` SSE subscriptions (`RunStreamBroker`) — clients (AdminConsole,
the steering client, any `Auxilia.Core.Client` app) hold ONE stream per observed run; there is no
SignalR backplane. Observing requires the `run.observe` policy check. Replay of a finished
run = read the persisted views ordered by Sequence and push them through the same
client-side renderer. (Artifact events have the analogous, server-side-filtered
`GET /api/artifacts/stream`.)

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

The Product MCP exposes `get_view_data(instanceId, viewName, fromSequence)` and a streaming
subscription backed by the same store/exchange — no hidden data channel.
