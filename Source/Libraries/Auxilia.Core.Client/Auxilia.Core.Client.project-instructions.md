# Auxilia.Core.Client

Typed HTTP client for the **entire** `Auxilia.Core.Api` REST surface — the supported way for any external application (Studio, a CLI, or a work app being integrated) to drive the Core. Nothing reaches the Core's database or bus directly.

## Invariants
- Talks to the Core over REST only, authenticating with the configured API key as a bearer token. No message-bus dependency.
- Speaks in `Auxilia.Core.Contracts` DTOs — it shares the wire contract with the server, so the two cannot drift. (Group / group-mapping / auth DTOs live in Contracts precisely so both server and client reference one definition.)
- Every non-success response surfaces as a **`CoreApiException`** carrying the `StatusCode` and the server's `{ error }` detail — callers distinguish 401/403/400 without inspecting raw responses. Get-by-id returns `null` on 404; `CheckHealthAsync` never throws on HTTP failures (transport errors do throw — poll under a caller-side guard).
- **Streams are resilient INSIDE the client** and yield a `ClientStreamFrame<TEvent>` union: `StreamEventFrame` payloads bracketed by `StreamConnectionFrame` (Connected / Reconnecting). The client reconnects with exponential backoff, treats an orderly close *without* a terminal status as a drop, enforces an idle timeout against the server's `: ping` keepalives (the same timeout also bounds the connect phase — a connection that never sends response headers is a drop, not a hang), and dedupes view frames across resubscribes by sequence **per view name** (view sequences are per-(run, view) monotonic — a single stream-global high-water mark would drop lagging views' frames). Raw transport exceptions NEVER escape the enumeration; non-transient HTTP errors (401/403/404) throw `CoreApiException`. Normal completion of `StreamRunAsync` reliably means the run reached a terminal state (the server re-delivers a status snapshot on every subscribe). Consumers must treat status frames as idempotent; on a `Connected` frame with `Attempt > 1` refresh caught-up state (refetch the run / `QueryArtifactsAsync` with `CreatedAfterUtc`).
- **`HttpClient.Timeout` is disabled by the client** (it would sever SSE streams); unary calls run under `CoreClientOptions.UnaryTimeoutSeconds` instead. Stream knobs: `StreamReconnectInitialBackoffSeconds` / `StreamReconnectMaxBackoffSeconds` / `StreamIdleTimeoutSeconds` (keep above the Core's `SseKeepaliveSeconds`).
- `ICoreClient` covers the full surface: runs, configurations, connectors (+ grants), groups, directory group→role mappings, artifacts (`QueryArtifactsAsync` / `GetArtifactAsync` / `OpenArtifactContentAsync` / `StreamArtifactEventsAsync` — the SSE stream is server-side filtered; chaining clients use THIS, never the message bus), and `GetCurrentPrincipalAsync` / `CheckHealthAsync`. Interactive OIDC sign-in (`/auth/login`, `/auth/callback`) is a **browser** redirect flow and is deliberately not part of the typed client — programmatic callers use an API key.
- `AddCoreClient` returns `IHttpClientBuilder` so a host can chain handlers; retry for unary calls may be layered by the host, but **stream resilience lives in the client itself** — never wrap the streams in caller-side retry loops.

## Integrating into an application
```csharp
// Static:
services.AddCoreClient("https://core.internal:8443", apiKey);
// Or from configuration:
services.AddCoreClient(o => configuration.GetSection("AuxiliaCore").Bind(o))   // CoreClientOptions
        .AddStandardResilienceHandler();
// Then inject ICoreClient anywhere.
```

## File / Folder Map
```
Source/Libraries/Auxilia.Core.Client/
├── ICoreClient.cs           # The full client abstraction (runs / configs / connectors / groups / mappings / identity)
├── CoreClient.cs            # HttpClient implementation; resilient SSE core (reconnect/idle/dedupe); CoreApiException helpers
├── StreamFrames.cs          # ClientStreamFrame union: StreamEventFrame / StreamConnectionFrame (Connected|Reconnecting)
├── CoreApiException.cs      # Non-success -> exception carrying StatusCode + ErrorDetail
└── CoreClientExtensions.cs  # AddCoreClient (baseAddress+apiKey / CoreClientOptions) + Create(HttpClient); CoreClientOptions
```
