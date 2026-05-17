# Auxilia.BackendService.Tests

Unit and component tests for `Auxilia.BackendService`. Also owns `FakeMessageBusClient` — the canonical in-process fake used by component tests across the whole solution.

## Special Rules
- `FakeMessageBusClient` is the single source of truth for fake bus behaviour; do not duplicate it elsewhere.

## File / Folder Map
```
Auxilia.BackendService.Tests/
├── UnitTests/
│   ├── QueueInitializerTests.cs                       # DeclareQueueAsync called on StartAsync; config override
│   ├── IdentificationRequestHandlerTests.cs            # Subscribe + respond round-trip (mocked bus)
│   └── IdentificationRequestHandlerTelemetryTests.cs   # Activity tags, counter increment, duration recorded
└── ComponentTests/
    ├── FakeMessageBusClient.cs             # IMessageBusClient fake: DeclaredQueues, PublishedMessages, SimulateReceivedAsync, WaitForConditionAsync
    ├── BackendServiceStartupTests.cs       # Real IHost + fake bus: queue declared, identification round-trip
    └── LogFileCreationTests.cs             # Serilog file sink created on startup
```