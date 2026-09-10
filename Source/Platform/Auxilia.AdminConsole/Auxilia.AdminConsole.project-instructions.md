# Auxilia.AdminConsole

The **operator/admin UI**: a Blazor Server (interactive server render mode) app that is a **pure
`Auxilia.Core.Client` consumer**. Dashboard, runs + run detail (live views over the Core's SSE
stream), events, workflows (configurations + editor), connectors, provider catalog, environments,
identity sources, principals/groups, platform settings, workflow registry, audit. It holds **no
database and no secrets** — every read and write is an `ICoreClient` call, authorized by the Core
as the signed-in operator.

## Load-bearing invariants (do not violate without asking)
- **Pure Core client.** No data access, no message bus, no vendor logic. If a page needs something
  the Core does not expose, the Core client surface grows (`docs/backlog.md` tracks gaps) — the
  console never side-steps it.
- **The Core is the only identity authority.** There is no local user store. Sign-in is the Core's
  browser flow (`RedirectToLogin` → Core `GET /auth/login`); sign-out is a POST to Core
  `/auth/logout`. Both the circuit-level `ConsoleAuthenticationStateProvider` and the endpoint-level
  `CoreBackedAuthenticationHandler` derive the principal from `ICoreClient.GetCurrentPrincipalAsync`
  and degrade to **anonymous** on 401 *and* on an unreachable Core (`HttpRequestException`) — a Core
  restart must never 500 the request or tear the circuit down. Roles surface as role claims and
  the Core-computed effective permissions as `auxilia:permission` claims; navigation and page
  actions are gated on those claims (`ConsoleAuthenticationStateProvider.Can`) as a show/hide
  convenience only — the Core re-authorizes every call.
- **Same-origin bearer handoff (deployment contract).** The console and Core.Api sit behind one
  gateway, so the browser's Core session cookie (`auxilia.core.session`) reaches the console. The
  console never sees a password: `ConsoleCallerTokenProvider` forwards that cookie to Core
  `POST /auth/token` (dedicated `core-auth` HttpClient, no bearer handler) and receives a
  short-lived per-user bearer, which `CoreCallerTokenHandler` attaches to every `ICoreClient` call.
- **Prerender → circuit relay.** The cookie is only readable while an `HttpContext` is live, i.e.
  during prerender; the interactive circuit runs over SignalR in a fresh DI scope with no
  `HttpContext`. So `ICoreClient` is composed **per scope** in `Program.cs` (a per-scope
  `CoreCallerTokenHandler` in front of a pooled primary handler — never through `AddCoreClient`'s
  long-lived handler scope), and the prerender-side provider relays its session across the boundary
  via `IUserBearerRelay`: `PersistentUserBearerRelay` stashes **token + session cookie** in the
  singleton `UserBearerHandleStore` and persists only a random **one-shot handle** into the page
  state. The circuit redeems the handle once and, because it now holds the cookie, **re-mints the
  bearer itself** whenever it expires. Neither the token nor the cookie ever rides the prerendered
  HTML/state blob; unredeemed handles die within minutes.
- **No service-key fallback on a user circuit.** The console holds NO `Core:ApiKey`, and
  `CoreCallerTokenHandler` sends *no* Authorization header when the provider yields nothing: a
  lapsed session becomes a 401/anonymous, never a silent switch to the bootstrap service principal
  (which would widen the operator's reach and shift audit attribution). When the Core rejects a
  re-mint mid-circuit (cookie session ended), `ConsoleCallerTokenProvider.SessionExpired` flips
  and `MainLayout` shows the "session has expired — reload" banner (sibling of the per-page
  `ConnectionBanner`); a full reload re-runs the prerender handoff or lands on the sign-in.
- **Ambient shell state is one loop per circuit.** `LiveRunMonitor` (Core health dot + active-run
  badge) and `StepUpFlow` (elevation prompt) are circuit-scoped; pages poll through `PagePoller`
  and stream through the client's resilient streams — no per-page health polling, no caller-side
  stream retry loops.
- **UI changes need a visual check**: compile-green is not done — launch the app and snap the
  changed page before finishing.
- `public partial class Program;` at the bottom of `Program.cs` supports `WebApplicationFactory`.

## Configuration
- `Core:BaseAddress` — the gateway/Core origin (same origin as the browser's Core session cookie).
  That is the whole configuration: the console carries no credential of its own.

## File / Folder Map
```
Source/Platform/Auxilia.AdminConsole/
├── Program.cs                     # Per-scope ICoreClient (caller-token handler over pooled primary), auth schemes, Blazor + view renderers
├── Auth/
│   ├── ConsoleCallerTokenProvider.cs      # cookie → POST /auth/token → per-user bearer; relay adoption + circuit re-mint; SessionExpired
│   ├── UserBearerRelay.cs                 # IUserBearerRelay + PersistentUserBearerRelay (PersistentComponentState + one-shot handle)
│   ├── UserBearerHandleStore.cs           # RelayedUserSession (token + cookie) behind a random single-use handle, singleton
│   ├── ConsoleAuthenticationStateProvider.cs  # circuit auth state from GetCurrentPrincipalAsync; permission claims; anonymous on failure
│   └── CoreBackedAuthenticationHandler.cs     # endpoint-level scheme (initial HTTP request of [Authorize] pages); challenge → Core sign-in
├── Components/
│   ├── App.razor, Routes.razor        # Interactive-server root; AuthorizeRouteView + RedirectToLogin
│   ├── Layout/MainLayout.razor        # Permission-gated sidebar, Core health dot, session-expired banner, sign-in/out
│   ├── Pages/*.razor                  # One page per Core surface (Dashboard, Runs, RunDetail, Events, Workflows, Connectors, Admin*, Audit)
│   ├── ConnectionBanner.razor         # Per-page "Core connection lost" banner (poll/stream disconnected)
│   ├── StepUpPrompt.razor, ConfirmButton.razor, Drawer.razor, SharingEditor.razor, ... # Shared building blocks
│   └── ViewRenderer.razor, AgentChatRenderer.razor  # Live-view rendering (descriptor-driven; BlazorAgentView for agent chat)
├── Rendering/                     # ViewRendererRegistry, descriptor heuristics, view item buffer
├── Support/                       # LiveRunMonitor, PagePoller, StepUpFlow, SharingDirectory, filters/formatting helpers
└── wwwroot/app.css                # The single stylesheet (fingerprinted via MapStaticAssets)
```
Tests live in `Tests/Platform/Auxilia.AdminConsole.Tests` (bUnit component tests over `FakeCoreClient`,
plus unit tests for the auth handoff).
