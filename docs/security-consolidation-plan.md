# Security Consolidation — Design & Migration Plan

> **Status:** Approved · 2026-07-25 · continues `docs/core-platform-separation-plan.md` (Phase 2's remaining intent)
> **Owner:** Felix Klakow
> **Goal:** Make "secrets live only in the Core" literally true. Today the Core owns connectors but they are *not* in the credential path — credentialed slots still resolve from the **runner's own** stores, seeded via the `slot-configurations` fanout. Move all credential resolution into the Core, keeping Core.Api and Core.Runner on **separate databases**.
>
> **Delivery status (2026-07-25):** **S1 ✅** (`6f31c2b`), **S2 ✅** (`3f486f4`), dispatcher decoupled (`7683f22`), and the **legacy removal is complete** (`609e547`): `SlotActivationHandler` is Core-only, and the runner's entire slot-config subsystem is deleted — `SlotConfigurationStore`, `WorkflowConfigurationStore`, `SlotInstanceStore`, `SlotConfigurationSeedHandler` + the `slot-configurations` fanout, `SlotConfigurationsSettings`, `DirtyConfigurationDetector`, `ConfigurationResolver` — with `WorkflowRegistrationHandler` rewired (persists the schema, resolves signal handlers, no slot-config validation). **The runner database now holds no connector secrets — "secrets only in the Core" is enforced, not just achievable.** All green (solution-wide unit + component). **Remaining: S4** (rate-limit + audit the resolution endpoint) and the credentialed **Docker acceptance** — wire `WorkflowDispatcher:CoreApiBaseAddress` into the `CoreApiDispatch` system-test environment and prove a real connector-backed run resolves JIT through the Core on Docker.

---

## 1. The gap (as-is)

- **Core.Api** owns connectors (`ConnectorService`: settings protected on write, `ResolveSettingsAsync` decrypts for JIT, reads expose keys only) — but `RunService.DispatchAsync` dispatches `RunWorkflowCommand(workflowType, packageUri, context)` with **no credential data**. Connectors are decorative w.r.t. dispatch.
- **Core.Runner** resolves every credentialed slot itself: `SlotActivationHandler` → `ConfigurationResolver.ResolveSlotAsync` / `ResolveConfigurationSlotAsync` read the runner's own `SlotConfigurationStore` / `WorkflowConfigurationStore` (seeded via the `slot-configurations` fanout) and RSA-encrypt from there.
- **Net:** the real secrets live in the **runner's** database. The separation's central security promise is unmet.

## 2. Target (to-be) — the Core resolves and encrypts; the runner only relays

Decisions (approved):
- **Separate databases** stay. The runner obtains resolved, per-instance-encrypted credentials **from Core.Api**, never from a shared store.
- **The Core encrypts** (not the runner): plaintext connector settings never enter the runner process. The RSA-encrypt-for-instance-public-key step moves into Core.Api next to `ResolveSettingsAsync`.
- **Channel:** runner↔Core resolution is **bus request/reply**, authorized by a **run-scoped resolution token** — so the runner (which holds the Docker socket) can only resolve slots for runs Core.Api actually dispatched to it, never arbitrary connectors.

```mermaid
sequenceDiagram
    participant Studio as Workflow Studio
    participant Api as Core.Api
    participant Runner as Core.Runner
    participant WF as Workload container
    Studio->>Api: create config (slot → connector REFERENCE)
    Studio->>Api: start run
    Note over Api: stash run resolution context<br/>(slot→connector bindings) keyed by runId;<br/>mint run-scoped resolution token
    Api->>Runner: RunWorkflowCommand (runId, resolutionToken — NO secrets)
    Runner->>WF: launch (one-time instance token, response queue)
    WF->>Runner: SlotActivationRequest (slot, ephemeral publicKey, instanceToken)
    Note over Runner: validate instance token (as today)
    Runner->>Api: ResolveSlotCredentialRequest (runId, slot, publicKey, resolutionToken)
    Note over Api: validate resolutionToken owns runId<br/>ResolveSettingsAsync(connectorId)<br/>RSA-encrypt for publicKey · audit
    Api-->>Runner: ResolveSlotCredentialResponse (EncryptedSlotConfiguration)
    Runner-->>WF: SlotActivationResponse (ciphertext, expiry)
```

The `slot-configurations` fanout, `SlotConfigurationSeedHandler`, and the runner's credential-bearing slot stores are then **deleted**.

## 3. Invariants (preserve / strengthen)

- One-time instance token + per-instance RSA encryption + exclusive response queue + credential expiry/re-request: **preserved**.
- Plaintext never leaves the Core: **strengthened** — the runner process holds no connector plaintext.
- Audit centralised in the Core: resolution + activation decisions audited in Core.Api.
- **Run-scoped authorization** of resolution: a runner can resolve only slots of runs it was dispatched — the socket-holder is not a skeleton key.
- Bus routing during co-existence honours the type-tag + filter discipline; verify on **real RabbitMQ** (the in-memory fake can't reproduce fanout cross-talk).

## 4. Phased migration (suite green at every step)

| Phase | Change | Tests |
|---|---|---|
| **S1** | Core.Api: `SlotCredentialResolver` (resolve via `ConnectorService` + RSA-encrypt-for-publicKey); per-run **resolution context** store keyed by runId; populate at dispatch from config/inline bindings; bus handler for `ResolveSlotCredentialRequest`. New messages in `Auxilia.Workflows.Messaging`. | unit (resolve+encrypt; token/run validation), component (endpoint returns ciphertext; plaintext never in reads/audit) |
| **S2** | Runner: `SlotActivationHandler` calls Core over the bus for credentialed slots and relays; `RunWorkflowCommand` carries runId + resolution token; token stored in `WorkflowInstanceRegistry`. | component (activation via a fake Core resolver), system (real connector → workflow, JIT, encrypted) |
| **S3** | Cut over: delete `SlotConfigurationSeedHandler` + the `slot-configurations` fanout + the runner's credentialed slot stores; migrate any secret-bearing runner settings into Core connectors (audited re-encrypt, **no plaintext logged**); Studio writes slot→connector references only. | system (no seed exchange; end-to-end), security review (grep logs/audit; assert runner DB holds no secrets) |
| **S4** | Harden: enforce the run-scoped resolution token; rate-limit + audit resolution calls. | component (unknown run/slot → reject; wrong token → 403) |

## 5. Risks

| Risk | Mitigation |
|---|---|
| Extra hop on JIT activation | Activation is already infrequent + async; creds re-resolve on expiry — fits the model. Resolve once per activation, not per call. |
| Bus cross-talk during co-existence | Type-tag + filter discipline; verify against real RabbitMQ, not the fake. |
| Secret migration (S3) | Re-encrypt runner slot settings into Core connectors in one audited step; assert no plaintext in logs/audit. |
| Runner as socket-holder resolving arbitrary connectors | Run-scoped resolution token (S4) — resolve only slots of dispatched runs. |

## 6. Done when

- A credentialed workflow dispatched through Core.Api receives its slot credentials **resolved and encrypted by Core.Api**, JIT, with the runner relaying ciphertext only.
- The `slot-configurations` fanout seed is gone; the runner database holds **no** connector secrets.
- Unit + component green; the `CoreApiDispatch` system path still runs a dummy workflow to Success; a credentialed system test proves the new resolution path.
