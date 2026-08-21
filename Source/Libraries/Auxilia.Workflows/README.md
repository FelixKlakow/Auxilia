# Auxilia.Workflows

The [Auxilia](https://github.com/FelixKlakow/Auxilia) workflow SDK: author the signed,
stateful workflow programs the platform runs in isolated containers.

Auxilia is a self-hosted platform for governed AI agent workflows: signed workflow
containers, just-in-time scoped credentials, live operator steering, and full REST + MCP
parity.

## What it gives you

- **`WorkflowBuilder`** — declare the workflow's whole contract in code: run inputs
  (rendered generically by every dispatch UI — see `WorkflowInputKinds`), outputs, views,
  triggers, events, repositories, network endpoints, companion containers, and the runtime
  pod-control envelope. `--emit-schema` prints the resulting schema for registration.
- **Typed run inputs** — the application reads its declared inputs through
  `IWorkflowInputs` (defaults, kind-aware parsing) instead of raw environment variables.
- **The runner handshake** — announcement, configuration, state reporting, slot
  activation, and the resource-proxy pattern (`IPodController`, steering, terminals) are
  all handled by `Run(args)`; your code is the application delegate.
- **Companions & pod control** — declare digest-pinned service containers and spawn more
  at runtime inside the signed envelope, on the run's private zero-egress pod network.

## Package family

| Package | Purpose |
| --- | --- |
| `Auxilia.Core.Contracts` | The wire contracts |
| `Auxilia.Core.Client` | Typed HTTP client for the full Core API |
| `Auxilia.Workflows.Client` | Workflow-domain client library (authoring, triggers, chaining) |
| `Auxilia.Workflows` | Workflow SDK for the programs themselves (this package) |
| `Auxilia.Messaging` | Message-bus abstraction + RabbitMQ implementation |
| `Auxilia.AI` | AI session abstraction for agentic workflow steps |
| `Auxilia.Steering.Codec` | Dependency-free steering wire protocol |

## License

Business Source License 1.1 — free for personal production use; commercial use requires a
license. See the packaged LICENSE file and the repository for terms.
