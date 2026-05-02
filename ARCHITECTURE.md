# Auxilia - Architecture Overview

> **Status:** Draft v0.3
> **Stack:** C# / ASP.NET Core / Blazor Server

---

## Table of Contents

1. System Overview
2. Core Concepts
3. High-Level Architecture
4. Key Components
5. Workflow Lifecycle
6. Security and Signing
7. Resource Access Model
8. Multi-Account Model
9. Deployment Modes
10. Open Questions and Concerns

---

## 1. System Overview

Auxilia is a **workflow-driven distributed system** where:

- **Work items** (from Jira, Azure DevOps, Trello, etc.) are the primary trigger for workflows.
- **Workflows** are stateful, signed programs that run to completion and communicate exclusively via the platform message bus.
- **AI agents** are first-class citizens - they can trigger, steer, observe, and complete workflows via the same interfaces as human users (MCP protocol).
- **Security is pluggable** - runs standalone out of the box, or integrates with corporate identity providers (AAD, LDAP, OIDC).

---

## 2. Core Concepts

| Concept | Description |
|---|---|
| **Work Item** | A unit of work from an external task source (Jira issue, ADO ticket, Trello card, etc.) |
| **Workflow** | A signed, stateful, isolated program that processes a work item through defined steps until completion or cancellation |
| **Steering Instance** | The backend mediator between the message bus, the frontend, and AI agents |
| **MCP Interface** | Model Context Protocol endpoint - gives AI agents full parity with the human UI |
| **Account Bundle** | A set of credentials grouped by purpose (e.g. user identity, AI service, source control) |
| **Resource Proxy** | A platform-managed, audited access point through which workflows reach external APIs and databases |
| **Trust Chain** | Signature chain ensuring a workflow is authorized to run in this environment |

---

## 3. High-Level Architecture

```mermaid
graph TB
    subgraph Sources["External Sources"]
        TASKS["Task Sources (Jira, ADO, TFS, Trello, GitHub)"]
        SC["Source Control (Git, TFVC)"]
        IDP["Identity Providers (AAD, LDAP, OIDC, Local)"]
        EXT["External Resources (APIs, Databases)"]
    end
    subgraph Clients["Clients"]
        UI["Blazor Server Frontend"]
        AI["AI Agents (Copilot, OpenAI, Custom)"]
    end
    subgraph Backend["Backend Platform (ASP.NET Core)"]
        GW["API Gateway"]
        MCP["MCP Server"]
        ORCH["Orchestration Service"]
        SEC["Security and Policy Service"]
        ACCT["Account Manager"]
        SIGNING["Workflow Trust Service (ISigningProvider)"]
        ADAPTERS["Integration Adapters"]
        PROXY["Resource Proxy"]
        BUS["Message Bus (IMessageBus - RabbitMQ default)"]
        SI["Steering Instance Pool"]
    end
    subgraph Execution["Workflow Execution (IWorkflowRunner)"]
        WR["Workflow Runner"]
        REG["Workflow Registry"]
        WF["Workflow Containers / Processes"]
    end
    UI <-->|HTTPS / SignalR| GW
    AI <-->|MCP Protocol| MCP
    MCP --> ORCH
    GW --> ORCH
    GW --> SEC
    ORCH --> SEC
    ORCH --> ACCT
    ORCH --> SIGNING
    ORCH --> ADAPTERS
    ORCH --> BUS
    BUS <--> SI
    SI <--> WF
    WF -->|Resource requests via bus| PROXY
    PROXY --> EXT
    SIGNING --> WR
    REG --> WR
    WR --> WF
    ADAPTERS <--> Sources
    IDP --> SEC
```

---

## 4. Key Components

### Frontend - Blazor Server
- Aggregated view of work items from all configured task sources
- Real-time workflow monitoring via SignalR
- Account bundle management and security policy configuration
- Workflow artifact configuration (where produced artifacts are routed)
- **Everything in the UI is also exposed via the MCP Server** - no hidden operations

### API Gateway - ASP.NET Core
- Single entry point for all external traffic (UI and MCP clients)
- Token validation delegated to the Security Service
- WebSocket / SignalR upgrade for real-time event streams

### Orchestration Service
- Receives work items from adapters and selects the appropriate workflow
- Pre-flight checks before dispatch: verifies signature, required account bundles, and resource proxy availability
- Manages the full workflow instance lifecycle (see section 5)
- Publishes commands and consumes status updates via IMessageBus

### Message Bus - IMessageBus
- **Default: RabbitMQ** (runs locally in Docker, zero-config)
- **Cloud swap: Azure Service Bus** (drop-in alternative via the same interface)
- All communication between Steering Instances and Workflow programs passes exclusively through here

### Steering Instance
- Bridges the message bus and the workflow runtime
- Injects scoped credentials and resource proxy endpoints at workflow startup
- Forwards AI assistance requests to the AI Integration Layer
- Streams live status to the frontend
- Notifies human / AI when a workflow is waiting for input due to resource unavailability

### Workflow Trust Service - ISigningProvider
- Verifies every workflow artifact before execution - no unsigned execution path exists
- In dev mode signing can be disabled entirely; no dev CA ceremony required locally
- Three production implementations depending on deployment mode (see section 9)
- Private key never touches the application process - only hash-in / signature-out

### Workflow Runner - IWorkflowRunner
- Abstracts how workflows are launched
- Three implementations depending on deployment mode (see section 9)

### Workflow C# SDK - NuGet Package
- Reference SDK for authoring workflows in C#; other language SDKs follow the same message contract
- Workflows declare: required bundle types, produced artifacts, and named resource dependencies
- Workflows compile to a self-contained executable, then are signed and pushed to the registry

### AI Integration Layer
- Routes AIAssistanceRequest messages (published by workflows) to the configured model
- Pluggable adapters: GitHub Copilot, Azure OpenAI, custom models
- Pre-flight check includes verifying the required AI bundle is available before dispatch
- All AI decisions are written to the audit log with full context

### Resource Proxy
- Platform-managed gateway through which workflows access external APIs and databases
- Workflow declares named dependencies (e.g. "github-api", "customer-db") at authoring time
- At runtime the Steering Instance resolves, scopes, and injects proxy endpoints - the workflow never holds raw credentials
- All proxy calls are audited; rate limiting and access policy enforced per workflow identity

### MCP Server
- Exposes every user-facing operation as an MCP tool
- AI agents authenticate with their own Account Bundle - subject to the same policy rules as humans
- Key tools: trigger_workflow, get_workflow_status, send_input, approve_step, cancel_workflow, list_work_items

---

## 5. Workflow Lifecycle

```mermaid
stateDiagram-v2
    [*] --> Received : Work item arrives
    Received --> PreFlight : Policy check passes
    PreFlight --> Queued : Signature valid, bundles available, resources reachable
    PreFlight --> PreFlightFailed : Required resource or bundle unavailable
    PreFlightFailed --> Queued : Condition resolved, requeued
    Queued --> Running : Steering Instance launches container / process
    Running --> WaitingForInput : Step requires human or AI decision
    WaitingForInput --> Running : Input received (human via UI or AI via MCP)
    Running --> Completed : All steps finished
    Running --> Failed : Unhandled error
    Running --> Cancelled : User or AI cancels
    Failed --> Queued : Retry policy if configured
    Completed --> [*]
    Cancelled --> [*]
    Failed --> [*]
```

**Key lifecycle properties:**
- Workflows are **stateful and long-running** - once started they run until Completed, Failed, or explicitly Cancelled
- A **pre-flight check** runs before any workflow is dispatched, verifying that signature, required account bundles, resource proxies, and AI accounts are all available
- If pre-flight fails the work item is held and the user/AI is notified; it can be requeued automatically when the condition resolves (configurable)
- **Workflow artifacts** are declared by the workflow and configured by the user - where they are stored and how they are linked back to the work item is a per-workflow configuration

---

## 6. Security and Signing

```mermaid
graph TD
    subgraph Workflow Trust
        DEV["Developer submits workflow"] --> SIGN_SVC["Trust Service signs artifact"]
        SIGN_SVC --> REG["Workflow Registry"]
        REG --> WR["Workflow Runner"]
        WR --> VERIFY["Signature verified before execution"]
        VERIFY -->|Valid| LAUNCH["Container / Process launched"]
        VERIFY -->|Invalid| REJECT["Execution rejected - always"]
        VERIFY -->|Dev mode - signing off| LAUNCH
    end
    subgraph Identity and Policy
        REQUEST["Any request - UI or MCP"] --> SEC["Security Service"]
        SEC --> IDP["Identity Provider (AAD / LDAP / OIDC / Local)"]
        IDP --> POLICY["Policy Engine"]
        POLICY -->|Allowed| ORCH["Orchestration"]
        POLICY -->|Denied| BLOCK["Blocked"]
    end
```

**Key principles:**
- Signing can be **fully disabled in dev mode** - there is no ceremony, no dev CA required locally
- In all non-dev modes signing is mandatory with no bypass; permissions are declared at signing time and cannot be escalated at runtime
- Workflows have no outbound network access of their own - all external access goes through the Resource Proxy
- Every action (human or AI) is written to an immutable audit log
- TFVC / TFS is explicitly supported as a source control target for enterprises that have not yet migrated

---

## 7. Resource Access Model

Workflows often need to reach external systems (APIs, databases, source control). Direct access is not permitted - all external access flows through the **Resource Proxy**.

```mermaid
graph LR
    WF["Workflow"] -->|Named resource request via message bus| PROXY["Resource Proxy"]
    PROXY --> ACCT["Account Manager (resolves credentials)"]
    PROXY --> POLICY["Policy Engine (is this workflow allowed to use this resource?)"]
    PROXY --> AUDIT["Audit Log"]
    PROXY -->|Scoped, time-limited call| EXT["External API / Database / Source Control"]
    EXT --> PROXY
    PROXY -->|Response via message bus| WF
```

**How it works:**
- The workflow declares named resource dependencies in its manifest (e.g. "github-api", "jira-instance", "customer-db")
- At runtime the platform resolves which Account Bundle and connection details map to that named resource for this user/context
- The workflow sends a resource request message and receives a response - it never holds a raw connection string, token, or API key
- The proxy enforces per-workflow rate limits and access policy; all calls are logged

---

## 8. Multi-Account Model

A user or service principal holds multiple **Account Bundles**, each scoped to a purpose.

```mermaid
graph LR
    USER["User / Service Principal"]
    USER --> ID["Primary Identity (AAD / LDAP / Local)"]
    USER --> SC_ACC["Source Control Bundle (Git PAT, TFVC credentials)"]
    USER --> TASK_ACC["Task Source Bundle (Jira OAuth, ADO PAT)"]
    USER --> AI_ACC["AI Bundle (Copilot, Azure OAI)"]
    USER --> RES_ACC["Resource Bundle (API keys, DB connections)"]
    ACCT_MGR["Account Manager"] -->|Resolves by purpose at runtime| USER
    WF["Workflow"] -->|Declares required bundle types| ACCT_MGR
```

- Workflows declare *which bundle types* they need - never specific credentials
- Bundles are encrypted at rest, decrypted only when injected into a workflow run via the Resource Proxy or Steering Instance
- AI bundles carry usage quotas and purpose restrictions
- Multiple bundles of the same type are supported (e.g. two ADO instances, a GitHub and a Jira account)

---

## 9. Deployment Modes

All modes share the same codebase. Runtime behaviour is driven entirely by configuration and pluggable interface implementations.

| Mode | Message Bus | Workflow Runtime | Signing | Identity |
|---|---|---|---|---|
| **Local / Dev** | RabbitMQ in Docker or in-process | OS Process (fully debuggable) | Disabled (no ceremony) | Local accounts |
| **On-premise** | RabbitMQ cluster | Docker / k3s | HashiCorp Vault Transit | LDAP / AD / OIDC |
| **Cloud (Azure)** | Azure Service Bus | AKS | Azure Key Vault | Entra ID |

Pluggable interfaces: **IMessageBus**, **IWorkflowRunner**, **ISigningProvider**, **IIdentityProvider**, **IResourceProxy**
The core platform has no hard dependency on RabbitMQ, Docker, Vault, or AAD.

---

## 10. Open Questions and Concerns

All previously raised architectural questions have been resolved and incorporated above.
The following minor points remain open for the detailed design phase:

| # | Question | Impact |
|---|---|---|
| 1 | **Resource Proxy transport** - Should resource request/response go through the main message bus or a dedicated side-channel? Main bus is simpler; side-channel allows lower latency for high-frequency database calls. | Performance, architecture |
| 2 | **Workflow artifact storage backend** - Where are produced artifacts stored? Options: blob storage (Azure Blob, S3-compatible, local filesystem), linked back to work item on completion. Should be configurable per deployment mode. | Workflow contract |
| 3 | **Pre-flight retry policy granularity** - Is retry-on-availability configured globally, per workflow, or per work item source? | UX, reliability |
| 4 | **TFVC scope** - Full branch/merge support confirmed as needed. Clarify whether TFS on-prem server versions down to TFS 2015/2017 are in scope or only TFS 2019+ / Azure DevOps Server. | Adapter implementation effort |

---

*Next step: Use Case document (USE-CASES.md)*

*Document maintained in ARCHITECTURE.md - update alongside major design decisions.*
