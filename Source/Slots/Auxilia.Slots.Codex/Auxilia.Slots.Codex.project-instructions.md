# Auxilia.Slots.Codex

Slot provider `codex-cli` for the `coding-agent` slot: spawns the OpenAI Codex CLI headless
(`codex exec --dangerously-bypass-approvals-and-sandbox --skip-git-repo-check …`) in the run's
workspace and streams its plain-text progress lines as `AgentChatEntry` items. Console mode
(the interactive CLI session) consumes the raw `CodingAgentCredentials` — CLI path plus
`OPENAI_API_KEY` in the session environment.

Builds `Auxilia.Slots.Codex.slothandler.dll` (see `<AssemblyName>`) with the
`*.slothandler.manifest.json` sidecar — the naming contract of `FileSystemPluginDiscovery`.
The manifest's `Settings` drive the visual editor: `ApiKey` (Secret), `Model`, `CliPath`
(points at a stub in system tests).

## Special rules

- The CLI child runs behind `ICliProcessFactory` (`Auxilia.Workflows.AiAgent.CodingAgent`; tests
  inject a fake). ANY exception leaving `RunAsync` — cancellation, a chat callback whose bus
  publish failed — kills the child's process tree before rethrowing; an exited child is never
  killed twice.
- The API key reaches the CLI ONLY via the child process environment (`OPENAI_API_KEY`) —
  never as a command-line argument, never in error messages, chat entries, or logs.
- The Codex CLI's exec mode offers no interactive control channel, so
  `CodingAgentRequest.Interaction` is ignored here — operator questions are a per-agent
  capability (the Claude provider implements them over its stdio control protocol).
- Bypassing the CLI's own sandbox/approvals is deliberate: the workflow container is the
  sandbox (default-deny egress, scoped workspace), and a headless run has nobody to answer
  approval prompts.
