# Auxilia.EgressGateway.Tests

Unit tests (`Category=Unit`) for the egress gateway: allowlist semantics, the special-purpose
address guard, forward parsing, and real proxied connections on loopback (CONNECT tunnel, a
denied destination's 403 and its logged denial, plain-HTTP origin-form forwarding). Loopback
targets are reachable in these tests only because they are listed as IP literals — exactly the
production rule. The full run topology (internal network, no direct route out) is proven by the
Docker system suite (`Tests/System/Auxilia.SystemTestSuite/Egress`).
