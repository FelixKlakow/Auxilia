# Auxilia.Workflows.Client

The [Auxilia](https://github.com/FelixKlakow/Auxilia) workflow-domain library on top of
`Auxilia.Core.Client`: schema-validated authoring, interval triggers, and artifact chaining
over the Core's filtered artifact event stream. Host it wherever you like — a server
service or embedded in a desktop app; triggers fire while your host runs.

Auxilia is a self-hosted platform for governed AI agent workflows: signed workflow
containers, just-in-time scoped credentials, live operator steering, and full REST + MCP
parity.

## What it gives you

- **Authoring** — configure runs of registered workflow types with client-side validation
  against the Core's workflow schemas before anything is dispatched.
- **Interval triggers** — dispatch a stored configuration on a schedule.
- **Artifact chaining** — trigger a workflow when another run produces a matching
  artifact, driven by the Core's filtered artifact event stream (never the message bus —
  this library is a pure Core client).

The platform ships `Auxilia.TriggerHost` as the bundled always-on reference host; this
package is the same engine for your own hosting choice.

## Package family

| Package | Purpose |
| --- | --- |
| `Auxilia.Core.Contracts` | The wire contracts |
| `Auxilia.Core.Client` | Typed HTTP client for the full Core API |
| `Auxilia.Workflows.Client` | Workflow-domain library (this package) |
| `Auxilia.Steering.Codec` | Dependency-free steering wire protocol |

## License

Business Source License 1.1 — free for personal production use; commercial use requires a
license. See the packaged LICENSE file and the repository for terms.
