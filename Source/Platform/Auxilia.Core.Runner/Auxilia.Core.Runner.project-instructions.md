# Auxilia.Core.Runner

The platform **execution plane** (formerly `Auxilia.SteeringInstance`). It runs workload containers correctly and owns a workflow instance's lifecycle: it consumes dispatch commands, launches the container, performs the authenticated registration handshake, mediates the workflow's resource / network / artifact / view traffic on the bus, and reports status back. It is deliberately "dumb" — it knows how to run a container securely, not what a workflow means.

In the Core-separation architecture the runner is driven by `Auxilia.Core.Api` over the message bus. The Core authorizes and resolves a run, then sends a **self-contained** `RunWorkflowCommand` (workflow type, package URI, context, slot **provider types**, a run-scoped **resolution token**, and the registry's **schema JSON** as a cold-start seed; `RequestedBy = null`). The dispatcher's schema-driven pre-launch decisions (interactive terminal, network baseline, declared repositories) read the runner's `WorkflowSchemaStore` first and fall back to the command's schema — seeding the store — so the FIRST run of a freshly registered type on a fresh runner already decides correctly (a run's own registration keeps refreshing the store afterwards). The runner needs **no configuration store** and cannot resolve Core-database principals; it trusts Core-dispatched commands. **Credentials live only in the Core:** at slot activation the runner asks Core.Api to resolve + encrypt the slot for the instance's ephemeral key (`CoreCredentialClient`) and relays the ciphertext — it stores no connector secrets. The run's **resolution token** (the bearer capability for those Core resolutions) is persisted inside the instance record's dispatch command only **protected at rest** (`DispatchCommandProtection`, same `ISettingsProtector` as the instance token); every reader that needs the live token unprotects symmetrically (slot activation, drain-replace). The runner keeps its own database, isolated from the Core's.

## Workload containment (2026-10-15)

Every launch is contained by data, never by trust in the workload (ARCHITECTURE §7 *Workload containment*):
- **Broker identity per run** — `WorkloadBusCredentials` mints a RabbitMQ user per instance through `IBrokerIdentityIssuer` (management API; the runner's own `RabbitMq` account must carry the administrator tag) with the permissions of `WorkloadBusAccess` (`Auxilia.Workflows.Messaging`): own queues + the `workflow.ingress` exchange (bound by every request handler via `WorkloadBusTopology.ExposeIngressQueueAsync`) + state/views/events. The container receives ONLY that identity (`RabbitMq__UserName/Password/IngressExchange`); `WorkloadBusTopology.DeclareAsync` declares every workload-facing exchange at startup because workloads cannot declare topology. Revoked on every terminal path.
- **Egress** — on a Linux daemon each run gets `Egress/DockerEgressHost`: its `--internal` network (`auxilia-run-<id>`) and the egress gateway (`auxilia-egress-<id>`, image `WorkflowLauncher:EgressGatewayImage`), the only container also on the outbound network. The workflow container joins the run network only (alias `workflow`); the dispatcher's `BuildEgressPlan` hands the gateway the allowlist (policy + workspace hosts + the run's slot providers' manifest `NetworkEndpoints` − `BlockedEndpoints` + `PlatformEgressEndpoints`) and the relays (broker, terminal, and one `tcp` relay per declared `tcp://host:port` endpoint — `Egress/TcpEndpoint`; the launcher maps each relayed name to the gateway's run-network address in the workload's host file, `WorkloadExtraHosts`). Plugin endpoint templates (`tcp://{Host}:{Port}`) are expanded by `NetworkPolicyResolver.ResolvePluginEndpointsAsync` from the run's non-secret setting values (`ICoreCredentialClient.ResolveSettingValuesAsync` → the Core's `/internal/runs/{id}/slot-settings`). Gateway events stream into `RunContainment.OnEgressEventAsync` (denials audited live, allowed destinations once, `EgressDenialLimit` kill switch → `HandleEgressViolationAsync`). A Windows daemon runs allow-all runs only.
- **Protected endpoints** — `BuildEgressPlan` hands the gateway `ProtectedEndpoints`: the broker's `RabbitMqHost:RabbitMqPort` always, plus `WorkflowLauncher:ProtectedEndpoints`. Never ban a whole host for the broker's sake — in dev, `host.docker.internal` also serves the sim git server.
- **Ownership labels** — `WorkflowLauncher:ContainerLabels` are stamped (`Stamp`, platform labels win) on every container, network and volume the runner creates — workflow containers, egress gateways and run networks, pod objects — so tooling (and the system tests' session sweep) selects exactly this runner's objects.
- **Runner posture** — `SandboxRuntime` picks the container runtime for workflow containers and companions (configured `ContainerRuntime`, else the first of `PreferredSandboxRuntimes` the daemon offers; `RequireSandboxRuntime` refuses a launch without one — unset it follows the posture via `ApplyPosture`: required once `TrustedSigningKeys` is set); a daemon that cannot enforce egress runs nothing unless `AllowUnenforcedEgress`; with `TrustedSigningKeys` set, `docker://` coordinates must be digest-pinned (`IsDigestPinned`).
- **Confinement** — `DockerWorkflowLauncher.ApplyConfinement`: `--cap-drop ALL` + `RetainedCapabilities` (or `ContainerCapabilities`), `no-new-privileges`, pids/memory/CPU ceilings, optional `ContainerRuntime`; companions drop raw sockets & co. and cannot escalate; `MapHostGateway` is opt-in.
- **Clone sources** — `WorkspaceManager` validates mount ids, branch names and the clone transport (`WorkflowDispatcher:AllowedCloneTransports`) before touching disk, passes `--` before sources, restricts git's transports per command, and sets `protocol.file.allow=never` on every clone. Push credentials never enter a clone: the dispatcher hands the push remote to the SDK only (`Workflow__PushRemote__<ID>`).
- **Trust & terminal** — `WorkflowDispatcher:TrustedSigningKeys` is the one publisher-trust list for environment fragments, workflow packages (`WorkflowPackageVerifier`, which also refuses unlisted entries) and slot plugins (`PluginIsTrustedAsync`); empty = dev posture. A served terminal gets a per-run credential (`Workflow__TerminalCredential` → ttyd `--credential`), handed to the Core only as the stamped endpoint's userinfo.
- **Evidence** — `RunContainment` releases the identity and the egress topology on every terminal path and persists the `egress-log` and `container-log` artifacts; `BusAuditRelay` forwards every runner audit record to the Core's central chained audit.

## Architecture
Durable runner state (workflow schemas, slot-handler provider registrations, signal handlers, run lifecycle records, audit log) lives in stores under `Workflows/Storage/` — async repositories over `IDataAccess<TEntity>` from `Auxilia.PlatformData` (backend per the `PlatformData` config section: Json default, MongoDb for replicated production, InMemory for tests). Only the package stores (`PendingWorkflowPackageStore` / `WorkflowPackageStore`, host-local paths) and `WorkflowInstanceTokenRegistry` (ephemeral one-time tokens) are in-memory by design — the instance record persists the **container id + protected instance token** precisely so a restarted runner can RE-ADOPT its containers (`WorkflowReadoptionService`, first startup step: re-claim under the fresh ServiceId, re-attach exit watchers, restore tokens, collect downtime exits, clean-kill the unmatchable). **The runner stores no slot configurations or connector secrets** — those belong to the Core.

The registration handshake is **authenticated by default** (`WorkflowDispatcherSettings.RequireInstanceToken`): the dispatcher issues a one-time instance token per launch (`WorkflowInstanceTokenRegistry`), injects it via `Workflow__InstanceId` / `Workflow__InstanceToken` env vars, and pre-creates the per-instance response queue. Announcement and registration handlers validate the token **and its workflow-type binding** (the announced / manifest name must equal the type the token was issued for — a run of type A can never rewrite type B's schema) and answer only on that canonical queue, ignoring the message's self-declared response topic. `WorkflowStateHandler` applies the same gate to terminal `WorkflowStateMessage`s (token + type) and then acts only on runs whose record this runner owns (`OwnerServiceId == CoreRunnerInfo.ServiceId`) — a missing or foreign record is ignored without a status event, audit entry, or cleanup, since the state exchange fans out to every runner. When several runners share a broker, `AnnouncementQueueName` / `RegistrationQueueName` must be unique per instance. `WorkflowRegistrationHandler` is started manually in `ApplicationStarted`, not as an `IHostedService`; it validates the environment, persists the manifest schema (`WorkflowSchemaStore`), and resolves signal handlers — it delivers **no** credentials (every slot activates just-in-time via the Core).

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
├── Program.cs                        # Host wiring (messaging, broker identity issuer, egress host, OTEL, Serilog, dispatch + handlers)
└── Workflows/
    ├── WorkflowDispatcher.cs         # Consumes RunWorkflowCommand; loads plugins for its provider types; launches; issues the one-time token
    ├── WorkflowCancelDispatcher.cs   # Consumes CancelWorkflowCommand from the shared queue; forwards it UNCONDITIONALLY to workflow-cancel-{id} (pool-safe — the consumer need not own the instance)
    ├── DockerWorkflowLauncher.cs / IWorkflowLauncher.cs / *DockerClientFactory.cs  # docker:// baked-image + package launch; labels + exit watchers
    ├── IWorkflowContainerHost.cs / WorkflowReadoptionService.cs  # Startup re-adoption: re-claim, watcher re-attach, token restore, clean-kill
    ├── WorkflowLaunchRequest.cs / WorkflowInstanceRegistry.cs  # Launch inputs; owned-instance registry (container id + protected token)
    ├── WorkflowAnnouncementHandler.cs / WorkflowRegistrationHandler.cs  # Authenticated announce + registration (env + schema + signal handlers)
    ├── SlotActivationHandler.cs / CoreCredentialClient.cs  # JIT slot activation: resolve + relay the Core-encrypted credential
    ├── EnvironmentValidator.cs / RunnerProfile.cs / ValidationResult.cs  # Manifest env requirements vs runner capabilities
    ├── WorkspaceManager.cs           # Warm cache (keyed by the credential-STRIPPED URL; credential injected per git
    │                                 #   command via -c url.<tokened>.insteadOf, never in .git/config; fetch + checkout
    │                                 #   advanced to the requested branch's tip, stale cache only on fetch failure)
    │                                 #   + per-run CoW repo snapshots and mounts; empty-workspace scratch dirs
    ├── NetworkPolicyResolver.cs      # Effective egress policy (manifest baseline + run config, clamped by platform ceiling)
    ├── WorkloadBus.cs                # Workload-facing topology (ingress exchange) + per-run broker identity issue/revoke
    ├── RunContainment.cs             # One run's broker identity + egress + evidence; live egress audit, kill switch
    ├── Egress/DockerEgressHost.cs    # Per-run --internal network + egress gateway (EgressPlan as data), live event feed
    ├── BusAuditRelay.cs              # Forwards every runner audit record to the Core's central chained trail
    ├── Pods/                         # Run pods (test-fabric design §A): PodPlanner (pure count/placeholder/DAG resolution)
    │                                 #   + DockerPodHost (per-run --internal network, companions, volumes, teardown/orphan sweep)
    │                                 #   + PodControlHandler/-Registry (runtime spawn/stop: token-authenticated queue, envelope
    │                                 #     clamps runtime-spawned containers only (auxilia.companion-runtime label; declared
    │                                 #     templates never consume it), configuration-pinned base map from PodBaseImagesJson)
    ├── ResourceProxyHandler.cs / IResourceConnector.cs  # Audited Resource Proxy calls on the workflow's behalf
    ├── ArtifactPersister.cs          # Persists declared artifact outputs to the Artifact Store
    ├── SignalDispatcher.cs / ViewDataHandler.cs / WorkflowStateHandler.cs  # Signals; live view data; WorkflowStateMessage
    ├── LongLivingDrainCoordinator.cs # Drain-and-replace for long-living workflows on config change / upgrade
    ├── DispatchCommandProtection.cs / DrainReplacement.cs / RunRootsCleanup.cs  # Shared: resolution-token at-rest protection;
    │                                 #   drain-replace dispatch (graceful AND drain-crash paths); best-effort sweep of
    │                                 #   workspace/pod/output root/extracted package on EVERY terminal path (graceful
    │                                 #   state, pre-flight failure, crash, re-adoption); the package (auxilia-wf-{id},
    │                                 #   bind-mounted :ro) dies with the container exit, never while it may still run
    ├── RunnerHeartbeatService.cs     # Emits ownership heartbeats for failover detection; advertises the
    │                                 #   Docker daemon's host platform (RunnerHostPlatformProbe, cached probe)
    └── Storage/                      # Durable stores over IDataAccess: schemas, slot-provider (DLL) registry, signal handlers,
                                      #   run instances, packages (PendingWorkflowPackageStore keyed by INSTANCE id — concurrent
                                      #   runs of one type never share a package) + in-memory WorkflowInstanceTokenRegistry
```
