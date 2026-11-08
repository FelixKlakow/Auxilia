# Auxilia.EgressGateway

The per-run egress gateway image (`auxilia-egress-gateway`). The Core.Runner starts one per run
(`Egress/DockerEgressHost`) on the run's `--internal` network (alias `egress`) and additionally
connects it — alone — to the operator's outbound network. It is the workflow container's ONLY
route out, and it knows nothing about providers, workflows or Auxilia's domain: everything it
enforces arrives as data.

## Configuration (environment, set by the runner)
- `EGRESS_ALLOW` — the run's effective allowlist: `host`, `host:port`, `*.suffix`, `*.suffix:port`,
  IP literals, http(s) URLs (reduced to host/port); `*` = allow-all. Empty = nothing.
- `EGRESS_FORWARDS` — fixed relays to platform services: `<listenPort>=<host>:<port>[/<kind>]`
  (the broker: `5672=<broker>:5672/bus`; the run's web terminal: `7681=workflow:7681/terminal`).
- `EGRESS_PORT` — proxy port (default 3128).
- `EGRESS_AMQP_USER` — the only broker identity relays of kind `amqp` admit (the run's own).
- `EGRESS_PROTECTED` — platform endpoints (`host[:port]`) refused before any allow entry, by name
  and by resolved address (another name for the broker is still the broker); other ports of the
  same host stay subject to the allowlist.
- Forward kinds: `amqp` (the broker, identity-pinned), `terminal` (the run's terminal), `tcp` (a
  declared raw-TCP endpoint — the target's addresses pass the same special-purpose rules as the
  proxy; a declared name counts as an explicit entry, so internal addresses are admitted).

## Behaviour
- HTTP proxy: `CONNECT host:port` tunnels and absolute-form `http://` requests. A tunnel's first
  client bytes are read before anything reaches the destination (the server's direction flows
  meanwhile, so server-first protocols work): a TLS ClientHello (`TlsClientHello` — fragmented
  records reassembled, malformed or duplicate server names refused, a stalled handshake refused
  after `HelloTimeout`) must name the tunnel's destination in its SNI or the tunnel is refused
  and logged as a denial (`sni` field) — no domain fronting; a handshake naming no server (an
  address literal) and non-TLS bytes pass, pinned to the admitted address. A plain request is
  re-issued in origin form with the TARGET's `Host` (the client's is ignored), hop-by-hop headers
  and `Proxy-*` dropped, and `Connection: close`, followed by exactly its `Content-Length` body —
  nothing the client sends after that (a pipelined second request, perhaps for another site on
  the same address) reaches the destination. A body framed by `Transfer-Encoding` has no length
  to hold it to and is refused (411, logged as a denial), as are conflicting lengths (400).
- Names are resolved by the gateway; a destination resolving only to loopback / link-local
  (cloud metadata) / multicast / unspecified addresses is refused even under allow-all — such an
  address is reachable only as an explicitly listed IP literal. Private and internal addresses
  (RFC 1918, CGNAT, unique-local — the platform's services, the operator network, other runs'
  gateways) are reachable only through an explicit entry, never through the `*` wildcard.
- An `amqp` relay reads the client's `Connection.Start-Ok` (PLAIN) before forwarding a byte of
  it: any login other than `EGRESS_AMQP_USER` is closed — a workload can never use a platform
  account through the relay, whatever credentials it holds.
- One JSON object per line on stdout per attempt (`connect` allow/deny/error, `close` with byte
  counts and duration, `forward`, `listening`). The runner follows the stream live (denials are
  audited immediately) and persists the whole log as the run's `egress-log` artifact.
- Runs unprivileged (`USER app`), read-only root filesystem, no capabilities.

## File / Folder Map
```
Source/Platform/Auxilia.EgressGateway/
├── Program.cs          # Env → policy, proxy, relays; "listening" event
├── EgressPolicy.cs     # Allowlist parsing/matching + special-purpose address guard
├── EgressProxy.cs      # CONNECT / absolute-form proxy, tunnel SNI check, TcpForward relays, the byte pipe
├── TlsClientHello.cs   # Strict ClientHello reader (server name for the tunnel check)
├── ForwardSpec.cs      # EGRESS_FORWARDS parsing
├── EgressLog.cs        # JSON-lines event writer
└── Dockerfile          # runtime:10.0, USER app, EXPOSE 3128
```
