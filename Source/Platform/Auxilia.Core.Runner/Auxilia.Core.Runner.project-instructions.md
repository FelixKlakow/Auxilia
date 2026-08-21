# Auxilia.Core.Runner

The platform **execution plane** (formerly `Auxilia.SteeringInstance`). It runs workload containers correctly and owns a workflow instance's lifecycle: it consumes dispatch commands, launches the container, performs the authenticated registration handshake, mediates the workflow's resource / network / artifact / view traffic on the bus, and reports status back. It is deliberately "dumb" — it knows how to run a container securely, not what a workflow means.

In the Core-separation architecture the runner is driven by `Auxilia.Core.Api` over the message bus. The Core authorizes and resolves a run, then sends a **self-contained** `RunWorkflowCommand` (workflow type, package URI, context, slot **provider types**, a run-scoped **resolution token**, and the registry's **schema JSON** as a cold-start seed; `RequestedBy = null`). The dispatcher's schema-driven pre-launch decisions (interactive terminal, network baseline, declared repositories) read the runner's `WorkflowSchemaStore` first and fall back to the command's schema — seeding the store — so the FIRST run of a freshly registered type on a fresh runner already decides correctly (a run's own registration keeps refreshing the store afterwards). The runner needs **no configuration store** and cannot resolve Core-database principals; it trusts Core-dispatched commands. **Credentials live only in the Core:** at slot activation the runner asks Core.Api to resolve + encrypt the slot for the instance's ephemeral key (`CoreCredentialClient`) and relays the ciphertext — it stores no connector secrets. The runner keeps its own database, isolated from the Core's.

## Architecture
Durable runner state (workflow schemas, slot-handler provider registrations, signal handlers, run lifecycle records, audit log) lives in stores under `Workflows/Storage/` — async repositories over `IDataAccess<TEntity>` from `Auxilia.PlatformData` (backend per the `PlatformData` config section: Json default, MongoDb for replicated production, InMemory for tests). Only the package stores (`PendingWorkflowPackageStore` / `WorkflowPackageStore`, host-local paths) and `WorkflowInstanceTokenRegistry` (ephemeral one-time tokens) are in-memory by design — the instance record persists the **container id + protected instance token** precisely so a restarted runner can RE-ADOPT its containers (`WorkflowReadoptionService`, first startup step: re-claim under the fresh ServiceId, re-attach exit watchers, restore tokens, collect downtime exits, clean-kill the unmatchable). **The runner stores no slot configurations or connector secrets** — those belong to the Core.

The registration handshake is **authenticated by default** (`WorkflowDispatcherSettings.RequireInstanceToken`): the dispatcher issues a one-time instance token per launch (`WorkflowInstanceTokenRegistry`), injects it via `Workflow__InstanceId` / `Workflow__InstanceToken` env vars, and pre-creates the per-instance response queue. Announcement and registration handlers validate the token and answer only on that canonical queue, ignoring the message's self-declared response topic. When several runners share a broker, `AnnouncementQueueName` / `RegistrationQueueName` must be unique per instance. `WorkflowRegistrationHandler` is started manually in `ApplicationStarted`, not as an `IHostedService`; it validates the environment, persists the manifest schema (`WorkflowSchemaStore`), and resolves signal handlers — it delivers **no** credentials (every slot activates just-in-time via the Core).

```mermaid
sequenceDiagram
    participant C as Core.Api
    participant D as WorkflowDispatcher
    participant W as Workflow container
    participant H as WorkflowRegistrationHandler
    participant A as SlotActivationHandler
    C->>D: RunWorkflowCommand (provider types + resolution token)
    D->>W: Launch container (one-time instance token injected)
    W->>H: WorkflowRegistrationRequest (+ ephemeral public key)
    H->>H: Validate environment; persist schema; resolve signal handlers
    H-->>W: WorkflowConfigurationResponse (no credentials)
    W->>A: SlotActivationRequest (slot, public key, instance token)
    A->>C: resolve slot credential (run-scoped token)
    C-->>A: EncryptedSlotConfiguration (encrypted for the instance)
    A-->>W: SlotActivationResponse (relayed ciphertext)
```

Slot-handler **plugins** (the `*.slothandler.dll` files, provider-type → DLL) are seeded into `SlotProviderRegistry` from `DockerWorkflowLauncher` startup config; the dispatcher loads the plugins for the run's provider types (carried in the command) before launch. There is no runtime slot-configuration seed and no `slot-configurations` fanout — that legacy subsystem was removed when credential resolution moved into the Core.

## File / Folder Map
```
Source/Platform/Auxilia.Core.Runner/
├── Program.cs                        # Host wiring (messaging, OTEL, Serilog, RunnerProfile, dispatch + handlers)
└── Workflows/
    ├── WorkflowDispatcher.cs         # Consumes RunWorkflowCommand; loads plugins for its provider types; launches; issues the one-time token
    ├── WorkflowCancelDispatcher.cs   # Consumes CancelWorkflowCommand; terminates the owned instance via its cancel queue
    ├── DockerWorkflowLauncher.cs / IWorkflowLauncher.cs / *DockerClientFactory.cs  # docker:// baked-image + package launch; labels + exit watchers
    ├── IWorkflowContainerHost.cs / WorkflowReadoptionService.cs  # Startup re-adoption: re-claim, watcher re-attach, token restore, clean-kill
    ├── WorkflowLaunchRequest.cs / WorkflowInstanceRegistry.cs  # Launch inputs; owned-instance registry (container id + protected token)
    ├── WorkflowAnnouncementHandler.cs / WorkflowRegistrationHandler.cs  # Authenticated announce + registration (env + schema + signal handlers)
    ├── SlotActivationHandler.cs / CoreCredentialClient.cs  # JIT slot activation: resolve + relay the Core-encrypted credential
    ├── EnvironmentValidator.cs / RunnerProfile.cs / ValidationResult.cs  # Manifest env requirements vs runner capabilities
    ├── WorkspaceManager.cs           # Warm cache + per-run CoW repo snapshots and mounts; empty-workspace scratch dirs
    ├── NetworkPolicyResolver.cs      # Effective egress policy (manifest baseline + run config, clamped by platform ceiling)
    ├── Pods/                         # Run pods (test-fabric design §A): PodPlanner (pure count/placeholder/DAG resolution)
    │                                 #   + DockerPodHost (per-run --internal network, companions, volumes, teardown/orphan sweep)
    │                                 #   + PodControlHandler/-Registry (runtime spawn/stop: token-authenticated queue, envelope
    │                                 #     clamps runtime-spawned containers only (auxilia.companion-runtime label; declared
    │                                 #     templates never consume it), configuration-pinned base map from PodBaseImagesJson)
    ├── ResourceProxyHandler.cs / IResourceConnector.cs  # Audited Resource Proxy calls on the workflow's behalf
    ├── ArtifactPersister.cs          # Persists declared artifact outputs to the Artifact Store
    ├── SignalDispatcher.cs / ViewDataHandler.cs / WorkflowStateHandler.cs  # Signals; live view data; WorkflowStateMessage
    ├── LongLivingDrainCoordinator.cs # Drain-and-replace for long-living workflows on config change / upgrade
    ├── RunnerHeartbeatService.cs     # Emits ownership heartbeats for failover detection; advertises the
    │                                 #   Docker daemon's host platform (RunnerHostPlatformProbe, cached probe)
    └── Storage/                      # Durable stores over IDataAccess: schemas, slot-provider (DLL) registry, signal handlers,
                                      #   run instances, packages + in-memory WorkflowInstanceTokenRegistry (one-time tokens)
```
