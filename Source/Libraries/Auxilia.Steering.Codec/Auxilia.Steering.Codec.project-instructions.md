# Auxilia.Steering.Codec

The steering wire protocol as a dependency-free shared library: typed `SteeringFrame`
records for the run's `steering` view (workflow → client: capabilities, forms, turn/session
boundaries, attention, session vocabulary) and for the operator's opaque inputs
(client → workflow: guidance, form answers, settings, halt, end), plus `SteeringCodec`
(encode / tolerant decode).

Invariants:
- **No dependencies** — desktop steering clients reference THIS instead of the full
  `Auxilia.Core.Contracts` surface. Never add a reference to another Auxilia library.
- **The protocol is additive.** `SteeringCodec.Decode` returns null for unknown frame
  types and malformed JSON — a newer peer must never break an older one. Never make
  decode throw; never remove or rename a wire property (`$type` names and JSON property
  names are the contract).
- `$type` is the discriminator and serializes first (`JsonPropertyOrder(-10)` on the base).
- The Core stays semantics-blind: these shapes are opaque to Core.Api/Core.Runner —
  nothing in the Core may reference this library.

Consumers in this repo: `Auxilia.Workflows` (`OperatorChannel` — the workflow side of the
loop) and `Auxilia.Workflows.AiAgent` (`ConsoleEventViews` — attention/turn frames).
Tests live in `Tests/Libraries/Auxilia.Steering.Codec.Tests`.
