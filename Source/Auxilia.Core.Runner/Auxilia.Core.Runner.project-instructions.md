# Auxilia.Core.Runner

The platform **execution plane** (formerly `Auxilia.SteeringInstance`). It runs workload containers correctly and owns a workflow instance's lifecycle: it consumes dispatch commands, launches the container, performs the authenticated registration + just-in-time credential handshake, mediates the workflow's resource / network / artifact / view traffic on the bus, and reports status back. It is deliberately "dumb" — it knows how to run a container securely, not what a workflow means.

In the Core-separation architecture the runner is driven by `Auxilia.Core.Api` over the message bus. The Core authorizes and resolves a run, then sends a **self-contained** `RunWorkflowCommand` (inline configuration, `RequestedBy = null`) — so the runner needs **no shared configuration store** and cannot resolve Core-database principals; it trusts Core-dispatched commands. The runner keeps its own database, isolated from the Core's.

## Architecture
Workflow state (schemas, slot configurations, providers, signal handlers, run lifecycle records, audit log) is **durable**: the store classes under `Workflows/Storage/` are async repositories over `IDataAccess<TEntity>` from `Auxilia.PlatformData` (backend per the `PlatformData` config section: Json default, MongoDb for replicated production, InMemory for tests). Slot settings are encrypted at rest via `ISettingsProtector` when `PlatformData:ProtectionKeyBase64` is set. Only the package stores (`PendingWorkflowPackageStore` / `WorkflowPackageStore`, host-local filesystem paths) and `WorkflowInstanceTokenRegistry` (ephemeral one-time tokens) remain in-memory by design.

The registration handshake is **authenticated by default** (`WorkflowDispatcherSettings.RequireInstanceToken`): the dispatcher issues a one-time instance token per launch (`WorkflowInstanceTokenRegistry`), injects it via `Workflow__InstanceId` / `Workflow__InstanceToken` env vars, and pre-creates the per-instance response queue. Announcement and registration handlers validate the token and answer only on that canonical queue, ignoring the message's self-declared response topic. When several runners share a broker, `AnnouncementQueueName` / `RegistrationQueueName` must be unique per instance — the launching instance holds the token, the pending package, and the slot configs.

```mermaid
sequenceDiagram
    participant C as Core.Api
    participant D as WorkflowDispatcher
    participant W as Workflow container
    participant H as WorkflowRegistrationHandler
    C->>D: RunWorkflowCommand (inline config)
    D->>W: Launch container (one-time token injected)
    W->>H: WorkflowRegistrationRequest (+ ephemeral public key)
    H->>H: Validate environment vs RunnerProfile; resolve + RSA-encrypt slot configs
    H-->>W: WorkflowConfigurationResponse
```

Schema drift: `DirtyConfigurationDetector` JSON-diffs an incoming `WorkflowSchema` against the stored one; if new required capability fields appear, all slot configs for that workflow type are marked `Dirty` and rejected until reconfigured.

Slot configurations and slot-provider plugin registrations are seeded at runtime via the `slot-configurations` **fanout** exchange (`UpsertSlotConfigurationCommand`, `RemoveSlotConfigurationCommand`, `RegisterSlotProviderCommand`, `RemoveSlotProviderCommand`); `SlotConfigurationSeedHandler` subscribes on startup. Multiple runner replicas each receive their own copy of every broadcast; each also exposes per-instance typed seed queues (`{CommandQueueName}-slot-seed.upsert|.remove|.register|.remove-provider`) for targeted delivery that bypasses the fanout. `WorkflowRegistrationHandler` is started manually in `ApplicationStarted`, not as an `IHostedService`.

## File / Folder Map
```
Source/Auxilia.Core.Runner/
├── Program.cs                        # Host wiring (messaging, OTEL, Serilog, RunnerProfile, dispatch + handlers)
└── Workflows/
    ├── WorkflowDispatcher.cs         # Consumes RunWorkflowCommand; launches the container; issues the one-time token
    ├── WorkflowCancelDispatcher.cs   # Consumes CancelWorkflowCommand; terminates the owned instance via its cancel queue
    ├── DockerWorkflowLauncher.cs / IWorkflowLauncher.cs / *DockerClientFactory.cs  # docker:// baked-image + package launch
    ├── WorkflowLaunchRequest.cs / WorkflowInstanceRegistry.cs  # Launch inputs; owned-instance registry
    ├── WorkflowAnnouncementHandler.cs / WorkflowRegistrationHandler.cs  # Authenticated announce + registration handshake
    ├── EnvironmentValidator.cs / RunnerProfile.cs  # Manifest env requirements vs runner capabilities (tools, OS, ports)
    ├── ConfigurationResolver.cs      # Loads stored configs, RSA-OAEP-encrypts each slot per instance
    ├── DirtyConfigurationDetector.cs / SchemaDiffResult.cs  # Schema diff -> mark slot configs dirty
    ├── SlotConfigurationSeedHandler.cs / SlotActivationHandler.cs  # Seed slot configs/providers; JIT slot activation
    ├── WorkspaceManager.cs           # Warm cache + per-run CoW repo snapshots and mounts
    ├── NetworkPolicyResolver.cs      # Effective egress policy (manifest baseline + run config, clamped by platform ceiling)
    ├── ResourceProxyHandler.cs / IResourceConnector.cs  # Audited Resource Proxy calls on the workflow's behalf
    ├── ArtifactPersister.cs          # Persists declared artifact outputs to the Artifact Store
    ├── SignalDispatcher.cs / ViewDataHandler.cs / WorkflowStateHandler.cs  # Signals; live view data; WorkflowStateMessage
    ├── LongLivingDrainCoordinator.cs # Drain-and-replace for long-living workflows on dirty config / upgrade
    ├── SteeringHeartbeatService.cs   # Emits ownership heartbeats for failover detection
    ├── ResolverResult.cs / ValidationResult.cs  # Result types
    └── Storage/                      # Durable stores over IDataAccess (schemas, slot configs, providers, signal handlers,
                                      #   instances, packages) + in-memory WorkflowInstanceTokenRegistry (one-time tokens)
```
