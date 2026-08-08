# Auxilia.Core.Contracts

Wire contracts (DTOs) of the [Auxilia](https://github.com/FelixKlakow/Auxilia) Core API —
runs, configurations, connectors, artifacts, platform events, workflow types, identity, and
audit. The same records are compiled into the server and every client, so the contract
cannot drift.

Auxilia is a self-hosted platform for governed AI agent workflows: signed workflow
containers, just-in-time scoped credentials, live operator steering, and full REST + MCP
parity.

## When to reference this package

- You are building against `Auxilia.Core.Client` (it references this package for you).
- You need the request/response shapes without any HTTP machinery — e.g. for serialization,
  test fakes, or your own transport.

## Package family

| Package | Purpose |
| --- | --- |
| `Auxilia.Core.Contracts` | The wire contracts (this package) |
| `Auxilia.Core.Client` | Typed HTTP client for the full Core API |
| `Auxilia.Workflows.Client` | Workflow-domain library: authoring, triggers, artifact chaining |
| `Auxilia.Steering.Codec` | Dependency-free steering wire protocol |

## License

Business Source License 1.1 — free for personal production use; commercial use requires a
license. See the packaged LICENSE file and the repository for terms.
