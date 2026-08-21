# Auxilia.Steering.Codec

The steering wire protocol shared by [Auxilia](https://github.com/FelixKlakow/Auxilia)
workflows and steering clients: typed frames of a run's steering view (questions, turn
boundaries, session vocabulary) and of the operator's opaque inputs (guidance, answers,
halt). Dependency-free — a desktop client can decode steering payloads without referencing
the full contracts surface.

Auxilia is a self-hosted platform for governed AI agent workflows: signed workflow
containers, just-in-time scoped credentials, live operator steering, and full REST + MCP
parity.

## Usage

`SteeringCodec.Encode` / `SteeringCodec.Decode` translate between the typed
`SteeringFrame` records and the JSON payloads that ride Auxilia's run views and run
inputs. Decoding is tolerant by design: unknown frame kinds and extra fields pass through
without throwing, so older clients keep working against newer workflows.

## Package family

| Package | Purpose |
| --- | --- |
| `Auxilia.Core.Contracts` | The wire contracts |
| `Auxilia.Core.Client` | Typed HTTP client for the full Core API |
| `Auxilia.Workflows.Client` | Workflow-domain library: authoring, triggers, artifact chaining |
| `Auxilia.Workflows` | Workflow SDK for the programs themselves |
| `Auxilia.Messaging` | Message-bus abstraction + RabbitMQ implementation |
| `Auxilia.AI` | AI session abstraction for agentic workflow steps |
| `Auxilia.Steering.Codec` | Dependency-free steering wire protocol (this package) |

## License

Business Source License 1.1 — free for personal production use; commercial use requires a
license. See the packaged LICENSE file and the repository for terms.
