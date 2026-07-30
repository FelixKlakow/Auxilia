# Auxilia

Auxilia is a workflow-driven distributed platform: work items from external task sources (Jira, ADO, Trello, GitHub) trigger **signed, stateful workflow programs** that run in isolated containers and communicate exclusively over a message bus. AI agents are first-class, with full UI parity through an MCP server.

The platform is three deployables sharing the `Auxilia.Core.Contracts` / `Auxilia.Core.Client` libraries:

- **`Auxilia.Core.Api`** — control plane: REST + authenticated MCP, identity/RBAC/groups, connector admin, audit, the Run API.
- **`Auxilia.Core.Runner`** — execution plane: container launch, network-egress policy, just-in-time credential delivery, run lifecycle.
- **`Auxilia.WorkflowStudio`** — the workflow-domain product, a pure Core client.

Each service owns its own database; secrets live only in the Core.

## License

**Auxilia is source-available, not Open Source.**

| | |
|---|---|
| **Free, no license needed** | Evaluation, testing, development, CI, proof of concept, teaching, personal use |
| **Requires a commercial license** | Any production use, including internal business tools |
| **Becomes Apache-2.0 on** | 2030-07-28 (this version) |

Auxilia is licensed under the [Business Source License 1.1](LICENSE) — the
same model used by MariaDB, Sentry and HashiCorp. You may read, fork, modify
and redistribute the code freely. You may run it as much as you like for
anything that is not production.

Once you want to run Auxilia in production, [buy a commercial
license](LICENSE-COMMERCIAL.md). On the Change Date above, this version
converts automatically to Apache-2.0 and the restriction disappears for good.

I use the term *source-available* deliberately rather than *Open Source*: the
[Open Source Definition](https://opensource.org/osd) forbids restrictions on
the field of use, and this license has one. Calling it Open Source would be
inaccurate.

Questions about whether your use needs a license? Open an issue or
discussion on this repository — I would much rather answer a question than
send an invoice to someone who got it wrong by accident.

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
