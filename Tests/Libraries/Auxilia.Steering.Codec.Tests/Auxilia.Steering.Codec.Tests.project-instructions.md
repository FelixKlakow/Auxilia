# Auxilia.Steering.Codec.Tests

Unit tests of the steering wire protocol: every frame round-trips through
`SteeringCodec.Encode`/`Decode`, `$type` serializes first, wire property names never
drift (they are asserted literally — a rename here is a protocol break, not a refactor),
and decode stays tolerant (unknown `$type`, unknown properties, malformed JSON → null).
