# Auxilia.Slots.GitHubCopilot

Slot provider `github-copilot-cli` for the `coding-agent` slot: spawns the GitHub Copilot CLI
headless (`copilot -p … --allow-all-tools --no-color`) in the run's workspace and streams its
plain-text progress lines as `AgentChatEntry` items.

Builds `Auxilia.Slots.GitHubCopilot.slothandler.dll` (see `<AssemblyName>`) with the
`*.slothandler.manifest.json` sidecar — the naming contract of `FileSystemPluginDiscovery`.
The manifest's `Settings` drive the visual editor: `token` (Secret, the GitHub account —
lower-case key so the shared git-credential resolvers match), `Model`, `CliPath` (points at
`/usr/local/bin/copilot-stub` in system tests).

## Special rules

- Containment: the SDK-driven CLI is launched through `AgentSandbox.WrapperFor(CliPath)` (unprivileged
  agent user) under an agent-session lease; the line-mode CLI goes through `ProcessCliProcessFactory`.
  Permission requests follow the permission mode; the session's `AgentSessionActionApprover` decides
  pushes at the SDK's push gateway (push policy) from their actual ref updates.

- The CLI child runs behind `ICliProcessFactory` (`Auxilia.Workflows.AiAgent.CodingAgent`; tests
  inject a fake). ANY exception leaving `RunAsync` — cancellation, a chat callback whose bus
  publish failed — kills the child's process tree before rethrowing; an exited child is never
  killed twice.
- The token is the person's: the slot binds their personal connector, and it stays current for
  the whole run — `CopilotTokenSource` re-reads the slot (`ISlotConfigurationSource`, a JIT
  activation through runner and Core) at most once a minute. SDK sessions get it through the
  Copilot SDK's session `GitHubTokenProvider` (GitHub.Copilot.SDK ≥ 1.0.29; the client-level token,
  used for the cached model list, is the one the session started on); the reported lifetime is
  the connector's `token_expires_at` (ISO 8601 or Unix time — whoever writes the token writes it)
  capped at 2 h and floored just above the runtime's 1 h refresh threshold, so the runtime asks
  about hourly, and every minute once the token is in its last hour. A connector that no longer
  holds a token ends the session; an unanswered re-read keeps the current token. Line mode starts
  each spawn on the current token; console (tmux) sessions keep the token they started with.
- **One SDK version** (`CopilotSdk.props`, imported here): every workflow image that hosts this slot imports
  `CopilotCli.props`, which pins the npm-installed CLI to the SDK's `CopilotCliVersion` (written on publish as
  `copilot-cli-version` for the Dockerfile) and records it for `ProvidesTools("copilot@<version>")`. Bump the
  SDK in `CopilotSdk.props` only, then rebuild the images. The SDK's bundled runtime
  (`runtimes/<rid>/native/copilot`) serves SDK sessions ONLY (`--server`/`--headless`; it refuses `-p` and
  `--version`) — the line and console modes need the full CLI, so the images ship the CLI, not that runtime.
- The token reaches the CLI ONLY via the child process environment (`GH_TOKEN`) — never as
  a command-line argument, never in error messages, chat entries, or logs.
- The Copilot CLI's headless mode offers no interactive control channel, so
  `CodingAgentRequest.Interaction` is ignored here — operator questions are a per-agent
  capability (the Claude provider implements them over its stdio control protocol).
