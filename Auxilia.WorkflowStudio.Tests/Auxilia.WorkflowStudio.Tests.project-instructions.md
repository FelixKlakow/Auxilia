# Auxilia.WorkflowStudio.Tests

Unit + component tests for `Auxilia.WorkflowStudio`.

## Isolation from the Core
`FakeCoreClient` implements `ICoreClient` in-memory so the Studio is tested without a running Core — it records the configurations / runs the authoring service would create and lets tests assert on them. Component tests host the real `Program` via `WebApplicationFactory` with an InMemory database and the fake client.

## File / Folder Map
```
Auxilia.WorkflowStudio.Tests/
├── FakeCoreClient.cs                       # In-memory ICoreClient; captures configure / run calls
├── UnitTests/
│   ├── WorkflowTypeCatalogTests.cs         # Register / list / get
│   └── WorkflowAuthoringServiceTests.cs    # Slot-binding validation, connector resolution, configure -> run
└── ComponentTests/
    └── StudioEndpointTests.cs              # /api/workflow-types + /api/configure over the hosted app
```
