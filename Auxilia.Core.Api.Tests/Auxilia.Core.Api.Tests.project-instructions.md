# Auxilia.Core.Api.Tests

Unit + component tests for `Auxilia.Core.Api`.

## How component tests are hosted
`CoreApiComponentTestBase` boots the real `Program` via `WebApplicationFactory`, then swaps infrastructure for in-memory fakes: `UseSetting("PlatformData:Backend", "InMemory")` for the isolated Core DB and `RemoveAll<IMessageBusClient>()` + a `FakeMessageBusClient` so no RabbitMQ is needed. Tests run the **real** auth + policy pipeline against a bootstrapped API key, so a call without the bearer token must 401 and an under-permissioned principal must 403.

## File / Folder Map
```
Auxilia.Core.Api.Tests/
├── CoreApiComponentTestBase.cs         # WebApplicationFactory host: InMemory DB + FakeMessageBusClient + seeded API key
├── FakeMessageBusClient.cs             # In-memory bus; captures dispatched RunWorkflowCommands
├── UnitTests/
│   ├── RunServiceTests.cs              # Dispatch shape (RequestedBy=null, inline command), cancel
│   ├── RunConfigurationServiceTests.cs # CRUD + idempotent EnsureAsync seeding
│   └── ConnectorServiceTests.cs        # Secrets encrypted on write, never returned by reads
└── ComponentTests/
    ├── CoreApiRunTests.cs              # POST /api/runs + configured run -> recorded in the run view
    ├── StaticConfigurationTests.cs     # Startup static-config seed is runnable
    ├── AuthTests.cs                    # Missing / unauthorized -> 401 / 403 on REST and MCP
    ├── GroupApiTests.cs                # create -> add-member -> assign-role reflected in list; unknown role 400
    └── CoreClientTests.cs              # Auxilia.Core.Client round-trips against the real API
```
