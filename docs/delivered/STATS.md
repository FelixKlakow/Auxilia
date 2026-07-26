# Auxilia — Stats & Facts

*As of 2026-07-09 (`feature/ton_of_features`).*

## Code

| Metric | Value |
| --- | ---: |
| Real code lines (no blanks, no comments) | **53,458** |
| — C# | 46,421 |
| — Razor (dashboard UI) | 5,745 |
| — CSS | 1,078 |
| — Dockerfiles & scripts | 214 |
| Production code (C#/Razor) | 25,803 |
| Test code (C#/Razor) | **26,363** |
| C# source files | 713 |
| Projects | 51 (21 of them test projects) |
| Commits | 177 (first: 2026-05-01) |

**More test code than production code** — 1,320 automated test cases across a
four-level pyramid (see `docs/TestStrategy.md`):

1. **Unit** — single class, mocked dependencies, runs on every commit (pre-commit hook)
2. **Component** — real DI container, in-memory infrastructure, no network
3. **System** — real MongoDB/RabbitMQ/containers via Testcontainers, images rebuilt from source
4. **Manual** — real external services, pre-release only

## Platform facts

- **Workflow-driven distributed system**: signed, stateful workflow programs run in
  isolated containers and communicate exclusively over the message bus.
- **AI agents are first-class citizens**: every dashboard operation is also an MCP
  tool — full UI parity for agents, policy-checked per principal.
- **Security by architecture**: default-deny network egress per workflow, credentials
  delivered just-in-time via the process environment (never CLI args, never logs),
  secrets write-only in the UI and encrypted at rest, every mutation audited without
  settings values.
- **Connectors**: sign in once (Claude account via the CLI's own OAuth flow, GitHub),
  scope it company-wide or personal, pick it wherever a slot needs that credential.
- **Per-run repository workspaces**: configured repositories are cloned per run by the
  platform (tokens scrubbed before the container ever sees the clone), warm-cached for
  public repos.
- **Live coding sessions**: the real Claude Code CLI in a web terminal (tmux + ttyd),
  proxied through the authenticated dashboard — browser disconnects never kill the session.
- **Autonomous Claude Code runs**: instruction in, agent chat streamed live to the
  dashboard, session report out — chainable into a summary mail via typed artifacts.
- **Runtime-extensible vocabularies**: providers, trigger kinds, connect flows, and
  account types are registered at runtime — adding one is a registration, not a rebuild.
- **One-click demo stand**: `Start-Presentation.bat` boots the full platform
  (MongoDB, RabbitMQ, mail server, Core.Runner, dashboard) in Docker with
  persistent data across restarts.
