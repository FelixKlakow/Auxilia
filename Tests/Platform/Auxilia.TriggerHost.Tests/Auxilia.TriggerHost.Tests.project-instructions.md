# Auxilia.TriggerHost.Tests

Component tests for `Auxilia.TriggerHost` — the thin always-on host composition, booted through
`WebApplicationFactory<Program>` (`public partial class Program` exists for exactly this).

## Special Rules
- Never reach a real Core: the primary `HttpMessageHandler` is replaced through
  `ConfigureHttpClientDefaults`, and `PlatformData:Backend` is set to `InMemory` so no JSON
  files land under the user's profile.
- The hosted engines and the email adapter start with the host — an empty in-memory store
  keeps them idle; do not add tests that depend on them contacting the (fake) Core.

## File / Folder Map
```
Tests/Platform/Auxilia.TriggerHost.Tests/
└── ComponentTests/
    └── CoreClientOptionsBindingTests.cs   # Core:* binds the whole CoreClientOptions (unary timeout observed against a silent Core)
```
