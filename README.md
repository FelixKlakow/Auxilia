# Auxilia

Auxilia is a workflow-driven distributed platform: work items from external task sources (Jira, ADO, Trello, GitHub) trigger **signed, stateful workflow programs** that run in isolated containers and communicate exclusively over a message bus. AI agents are first-class, with full UI parity through an MCP server.

The platform is three deployables sharing the `Auxilia.Core.Contracts` / `Auxilia.Core.Client` libraries:

- **`Auxilia.Core.Api`** — control plane: REST + authenticated MCP, identity/RBAC/groups, connector admin, audit, the Run API.
- **`Auxilia.Core.Runner`** — execution plane: container launch, network-egress policy, just-in-time credential delivery, run lifecycle.
- **`Auxilia.WorkflowStudio`** — the workflow-domain product, a pure Core client.

Each service owns its own database; secrets live only in the Core.

## Getting started

Requires .NET 10. Solution file: `Auxilia.slnx`.

```powershell
dotnet build Auxilia.slnx        # build everything
dotnet test  Auxilia.slnx        # full test suite
```

System tests need Docker running (`dotnet test Auxilia.SystemTestSuite/ --filter "Category=System"`).

## Documentation

- **`docs/ARCHITECTURE.md`** — full architecture, the credential/trust model, dispatch lifecycle, security.
- **`CLAUDE.md`** — working guidance and the documentation map.
- **`docs/TestStrategy.md`** — the test pyramid.
- **`docs/`** — live design docs; **`docs/delivered/`** — delivered and historical records (context only).
