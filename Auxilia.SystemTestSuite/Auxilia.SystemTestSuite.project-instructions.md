# Auxilia.SystemTestSuite

End-to-end system tests. Each scenario has a `[SetUpFixture]` that builds Docker images from source, creates a network, starts containers (RabbitMQ + service-under-test), and exposes a live `IMessageBusClient` to tests. Tests use real messages against real containers.

## File / Folder Map
```
Auxilia.SystemTestSuite/
├── SingleBackendService/
│   ├── SingleBackendServiceEnvironment.cs   # SetUpFixture: RabbitMQ + one BackendService container
│   └── BackendServiceSystemTests.cs         # Queue declared; identification round-trip
├── DualBackend/
│   ├── DualBackendServiceEnvironment.cs     # SetUpFixture: RabbitMQ + two BackendService containers
│   ├── DualBackendServiceSystemTests.cs     # Multi-instance routing
│   └── DualBackendServiceTelemetryTests.cs  # OTEL metrics/traces across two instances
└── MongoDb/
    ├── MongoDbEnvironment.cs                # SetUpFixture: MongoDB container
    └── MongoDbSystemTests.cs                # CRUD round-trips via MongoDbEfDataAccess
```