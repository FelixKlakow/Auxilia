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

- The CLI child runs behind `ICliProcessFactory` (`Auxilia.Workflows.AiAgent.CodingAgent`; tests
  inject a fake). ANY exception leaving `RunAsync` — cancellation, a chat callback whose bus
  publish failed — kills the child's process tree before rethrowing; an exited child is never
  killed twice.
- The token reaches the CLI ONLY via the child process environment (`GH_TOKEN`) — never as
  a command-line argument, never in error messages, chat entries, or logs.
- The Copilot CLI's headless mode offers no interactive control channel, so
  `CodingAgentRequest.Interaction` is ignored here — operator questions are a per-agent
  capability (the Claude provider implements them over its stdio control protocol).
