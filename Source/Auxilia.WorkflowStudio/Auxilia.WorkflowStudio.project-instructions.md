# Auxilia.WorkflowStudio

The workflow-domain **product**: it owns what a workflow *is* (workflow types and their declared slots) and lets a user author a runnable configuration and dispatch it. It is a **pure `Auxilia.Core.Client` consumer** — it holds no credentials, touches no message bus, and shares no database with the Core. Running and connector resolution happen by calling the Core.

Standalone deployable with its own database (workflow types only).

## Invariants
- **Drives the Core only through `ICoreClient`** (`AddCoreClient`) — never the bus, never the Core's DB. This is the point of the separation: the product is a Core client.
- **Own database, workflow-domain only.** Registers `StudioWorkflowTypeRecord`; never a Core entity.
- **Authoring validates before it dispatches.** `WorkflowAuthoringService` checks each `SlotBinding` against the workflow type's *declared* slots and resolves connector references via the Core, then calls `CreateConfigurationAsync` + `RunConfigurationAsync`. Bad slot binding -> `ArgumentException` (400); unknown type/connector -> `KeyNotFoundException` (404).
- `public partial class Program;` supports `WebApplicationFactory` component tests.

## REST surface
- `POST` / `GET /api/workflow-types`, `GET /api/workflow-types/{name}` — the workflow-domain catalog
- `POST /api/configure` — author + dispatch (produces and runs a Core run configuration)
- `POST /api/configured/{coreConfigId}/run` — re-run a previously authored configuration
- `GET /health`

## File / Folder Map
```
Source/Auxilia.WorkflowStudio/
├── Program.cs                       # Own DB; AddCoreClient; workflow-type + configure/run endpoints
├── StudioContracts.cs               # RegisterWorkflowType, ConfigureWorkflow, and DTOs
├── Data/
│   └── StudioWorkflowTypeRecord.cs  # Workflow type + declared slots (Studio-owned)
└── Services/
    ├── WorkflowTypeCatalog.cs       # Register / list / get workflow types
    └── WorkflowAuthoringService.cs  # Validate slot bindings, resolve connectors via ICoreClient, configure + run
```
