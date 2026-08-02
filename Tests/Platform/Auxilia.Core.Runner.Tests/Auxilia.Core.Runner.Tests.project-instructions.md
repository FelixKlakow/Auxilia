# Auxilia.Core.Runner.Tests

Unit + component tests for `Auxilia.Core.Runner` (the execution plane, formerly `Auxilia.SteeringInstance`).

Component tests (`ComponentTests/`, `Category=Component`) boot the runner's handlers over a `FakeMessageBusClient` and a `FakeWorkflowLauncher` (no Docker, no network) and drive whole pipelines end-to-end: dispatch, the authenticated registration handshake, JIT slot activation, cancel, and durable platform-state persistence. Everything else is class-level unit tests (`Category=Unit`) over the stores, handlers, and Docker-launch argument building.

## File / Folder Map
```
Tests/Platform/Auxilia.Core.Runner.Tests/
├── ComponentTests/
│   ├── FakeMessageBusClient.cs / FakeWorkflowLauncher.cs   # In-memory bus + launcher (no Docker)
│   ├── WorkflowDispatchPipelineComponentTests.cs           # RunWorkflowCommand -> launch
│   ├── WorkflowConfigurationDispatchComponentTests.cs      # Inline-config dispatch from Core.Api
│   ├── AuthenticatedHandshakeComponentTests.cs             # One-time token announce + registration
│   ├── JitSlotActivationComponentTests.cs                  # Just-in-time slot activation
│   ├── WorkflowCancelPipelineComponentTests.cs             # CancelWorkflowCommand -> terminate
│   └── PlatformStatePersistenceComponentTests.cs           # Durable stores survive restart
├── Workflows/                                              # Handler + component-part unit tests
│   ├── WorkflowDispatcher*Tests.cs                         # Config repo/inline, docker-image URI, workspace, run-output bind
│   ├── DockerWorkflowLauncher{Arg,BakedImage,CopyIn}Tests.cs
│   ├── WorkflowAnnouncementHandlerTests.cs / WorkflowRegistrationHandlerTests.cs
│   ├── WorkflowCancelDispatcherTests.cs / WorkflowStateHandlerTests.cs
│   ├── SlotActivationHandlerTests.cs / SlotConfigurationSeedHandlerTests.cs / SlotInstanceStoreTests.cs
│   ├── NetworkPolicyResolverTests.cs / ResourceProxyHandlerTests.cs / ArtifactPersisterTests.cs
│   ├── ViewDataHandlerTests.cs / LongLivingDrainCoordinatorTests.cs
│   └── WorkflowInstanceTokenRegistryTests.cs / WorkflowPackageStoreTests.cs / WorkspaceManagerTests.cs
├── TestStores.cs                                           # Shared in-memory store builders
├── EnvironmentValidatorTests.cs / ConfigurationResolverTests.cs / DirtyConfigurationDetectorTests.cs
├── SlotConfigurationStoreTests.cs / SlotProviderRegistryTests.cs
├── WorkflowConfigurationStoreTests.cs / WorkflowSchemaStoreTests.cs
└── SignalDispatchPhaseTests.cs
```
