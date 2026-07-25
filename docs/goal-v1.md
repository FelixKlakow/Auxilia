# Goal: Auxilia v1 — Complete Platform Foundation, Proven by One Real End-to-End Path

> Cross-reference: ARCHITECTURE.md, USE-CASES.md, docs/workflow-sdk-design.md
>
> **Historical (v1 / demo goal).** This records the pre-separation v1 goal, achieved for the demo. The platform has since been split into the three-deployable Core architecture — see ARCHITECTURE.md and `docs/core-platform-separation-plan.md`. Where this doc says *Steering Instance*, read `Auxilia.Core.Runner`.

Build the complete Auxilia platform as specified in ARCHITECTURE.md, so that one real, governed, fully observable workflow runs end to end — from an external trigger to a persisted, re-viewable result — with every platform guarantee enforced and proven by the automated test pyramid.

## In scope

- Every platform component:
  - Durable **Platform Data Layer** (replacing the interim in-memory Steering Instance stores)
  - **Identity, accounts and Policy Engine** — administrators, operators, users, AI principals
  - **Authenticated registration handshake** (one-time instance token + exclusive response queue)
  - **Just-in-time per-slot credential delivery** with expiry and transparent re-request
  - The **dispatch / lifecycle / failover control plane** (Steering Instance + Backend Service host: scheduler, heartbeat monitor, SignalR fan-out)
  - **Long-living workflow** support (drain-and-replace, credential renewal, operator opt-in)
  - **IArtifactStore** (filesystem backend + metadata index, lineage resolution, read-only consumption mounts)
  - **Live view data** and persisted view replay
  - The full **Blazor dashboard** including generic descriptor-driven view rendering, dashboard composition, and administration
  - **MCP server with full UI parity** for AI agents
  - The **execution isolation layer** — Workspace Manager, Resource Proxy, Network Egress
- Exactly **one workflow**: Code Review, serving as the test workflow that exercises every platform capability (trigger, pre-flight, credentials, AI slot, live views, artifact persistence, write-back).
- Exactly **one production-grade task source**: an **email adapter (IMAP, Gmail-compatible)** that turns incoming mail into work items — system-tested against a containerized mail server, manually verifiable against a real Gmail account.

## Out of scope

- All other use-case workflows from USE-CASES.md (Selective Fixing, Refinement, Planning, Implementation, Security Scan/Fix).
- All other task-source adapters (Jira, ADO, Trello, GitHub), the Package Proxy, and the Work Item Index.

## Done when

- `dotnet test Auxilia.slnx` is green at all pyramid levels, and the system test suite covers every platform guarantee: dispatch and lifecycle transitions, failover with a visible restart, RBAC enforcement including AI parity via MCP, credential expiry and re-request, artifact lineage resolution, live view streaming plus replay of finished runs, and audit-log completeness.
- An operator can configure accounts and policies in the dashboard, connect the email source, watch a mail-triggered Code Review run live, and re-open its results after completion — and an AI agent can do all of the same through MCP.

## Implementation order

The phased backlog (tasks #1–#15 in the task system):

| Phase | Work |
|---|---|
| 1 | Governance & RBAC design · authenticated handshake |
| 2 | Durable platform persistence layer |
| 3 | Identity, accounts and Policy Engine |
| 4 | Dispatch, lifecycle and failover (Steering Instance + Backend Service host) |
| 5 | Long-living workflow lifecycle · JIT per-slot credentials with expiry · IArtifactStore |
| 6 | View-data design · Blazor dashboard · MCP server parity · email task-source adapter |
| 7 | Execution isolation layer (Workspace Manager, Resource Proxy, Network Egress) |
| 8 | End-to-end Code Review use case + full system test coverage |
