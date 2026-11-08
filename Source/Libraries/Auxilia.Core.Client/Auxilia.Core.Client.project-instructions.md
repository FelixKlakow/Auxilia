# Auxilia.Core.Client

Typed HTTP client for the **entire** `Auxilia.Core.Api` REST surface — the supported way for any external application (Studio, a CLI, or a work app being integrated) to drive the Core. Nothing reaches the Core's database or bus directly.

## Invariants
- Talks to the Core over REST only, authenticating with the configured API key as a bearer token. No message-bus dependency.
- **Delegated clients never downgrade to a service key.** The per-caller overload `AddCoreClient(configure, tokenProviderFactory)` chains a `CoreCallerTokenHandler` that sets `Authorization` from an `ICoreCallerTokenProvider` per request; when the provider yields no token the request goes out with NO credential (the Core answers 401) — `CoreClientOptions.ApiKey` is ignored on that path. A host that needs service-scoped calls registers a separate client through the app-key overload; there is no "user token, else app key" mode.
- Speaks in `Auxilia.Core.Contracts` DTOs — it shares the wire contract with the server, so the two cannot drift. (Group / group-mapping / auth DTOs live in Contracts precisely so both server and client reference one definition.)
- Every non-success response surfaces as a **`CoreApiException`** carrying the `StatusCode` and the server's `{ error }` detail — callers distinguish 401/403/400 without inspecting raw responses. Get-by-id returns `null` on 404; `CheckHealthAsync` never throws on HTTP failures (transport errors do throw — poll under a caller-side guard).
- **Streams are resilient INSIDE the client** and yield a `ClientStreamFrame<TEvent>` union: `StreamEventFrame` payloads bracketed by `StreamConnectionFrame` (Connected / Reconnecting). The client reconnects with exponential backoff, treats an orderly close *without* a terminal status as a drop, enforces an idle timeout against the server's `: ping` keepalives (the same timeout also bounds the connect phase — a connection that never sends response headers is a drop, not a hang), and **resumes the run stream** from the last yielded event's `RunStreamEvent.Cursor` (sent as `Last-Event-ID`; `StreamRunAsync(runId, after:)` starts from a kept one) — the Core then delivers every frame after it exactly once, in order, so a run stream needs no client-side backfill or merge (a residual dedupe by (view, sequence) stays as a guard). A 204 ends a stream for good (a finished run, nothing more). Raw transport exceptions NEVER escape the enumeration; non-transient HTTP errors (400/401/403/404) throw `CoreApiException`. Normal completion of `StreamRunAsync` reliably means the run reached a terminal state (its verdict is the last frame). Status frames carry the run's state, not every transition. Artifact and event streams do not resume: on a `Connected` frame with `Attempt > 1` catch up (`QueryArtifactsAsync`/`QueryEventsAsync` with `CreatedAfterUtc`).
- **`HttpClient.Timeout` is disabled by the client** (it would sever SSE streams); unary calls run under `CoreClientOptions.UnaryTimeoutSeconds` instead, and a watchdog expiry surfaces as **`TimeoutException`** (inner `OperationCanceledException`) — NEVER as the caller's cancellation. Long-running consumers (trigger engines, intake adapters) key "host shutdown" on their own token (`ct.IsCancellationRequested`), so a slow Core must not wear that shape; only the caller's own token produces an `OperationCanceledException`. Stream knobs: `StreamReconnectInitialBackoffSeconds` / `StreamReconnectMaxBackoffSeconds` / `StreamIdleTimeoutSeconds` (keep above the Core's `SseKeepaliveSeconds`).
- `ICoreClient` covers the full surface: runs (incl. `SetRunGrantsAsync` — runs are reachable only by the principal they run for, their delegating starter, their grantees and administrators; anything else is a 404 `CoreApiException`), configurations, connectors (+ grants), groups, directory group→role mappings, artifacts (`QueryArtifactsAsync` / `GetArtifactAsync` / `OpenArtifactContentAsync` / `StreamArtifactEventsAsync` — the SSE stream is server-side filtered; chaining clients use THIS, never the message bus — and `DeleteArtifactAsync`, which deletes a run CHECKPOINT only: `ArtifactDto.Kind == "checkpoint"`, kept until `ExpiresUtc`, deletable by the run's own principals and administrators; false when unknown, a refusal throws), and `GetCurrentPrincipalAsync` / `CheckHealthAsync`. Interactive OIDC sign-in (`/auth/login`, `/auth/callback`) is a **browser** redirect flow and is deliberately not part of the typed client — programmatic callers use an API key.
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
