# Auxilia.Core.Client

Typed HTTP client for the **entire** `Auxilia.Core.Api` REST surface — the supported way for any external application (Studio, a CLI, or a work app being integrated) to drive the Core. Nothing reaches the Core's database or bus directly.

## Invariants
- Talks to the Core over REST only, authenticating with the configured API key as a bearer token. No message-bus dependency.
- Speaks in `Auxilia.Core.Contracts` DTOs — it shares the wire contract with the server, so the two cannot drift. (Group / group-mapping / auth DTOs live in Contracts precisely so both server and client reference one definition.)
- Every non-success response surfaces as a **`CoreApiException`** carrying the `StatusCode` and the server's `{ error }` detail — callers distinguish 401/403/400 without inspecting raw responses. Get-by-id returns `null` on 404; `CheckHealthAsync` never throws.
- `ICoreClient` covers the full surface: runs, configurations, connectors (+ grants), groups, directory group→role mappings, and `GetCurrentPrincipalAsync` / `CheckHealthAsync`. Interactive OIDC sign-in (`/auth/login`, `/auth/callback`) is a **browser** redirect flow and is deliberately not part of the typed client — programmatic callers use an API key.
- `AddCoreClient` returns `IHttpClientBuilder` so a host can chain resilience/handlers (e.g. `.AddStandardResilienceHandler()`).

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
Auxilia.Core.Client/
├── ICoreClient.cs           # The full client abstraction (runs / configs / connectors / groups / mappings / identity)
├── CoreClient.cs            # HttpClient implementation; central error-handling helpers throw CoreApiException
├── CoreApiException.cs      # Non-success -> exception carrying StatusCode + ErrorDetail
└── CoreClientExtensions.cs  # AddCoreClient (baseAddress+apiKey / CoreClientOptions) + Create(HttpClient); CoreClientOptions
```
