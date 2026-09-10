# Auxilia.Slots.ClaudeCode

Slot provider `claude-code-cli` for the `claude-code` workflow's `coding-agent` slot: spawns
the Claude Code CLI headless (`-p … --output-format stream-json --verbose`) in the run's
workspace and maps its transcript onto `AgentChatEntry` items.

Builds `Auxilia.Slots.ClaudeCode.slothandler.dll` (see `<AssemblyName>`) with the
`*.slothandler.manifest.json` sidecar — the naming contract of `FileSystemPluginDiscovery`.
The manifest's `Settings` descriptors drive the visual configuration editor: `OAuthToken`
(Secret — the connected Claude account, preferred), `ApiKey` (Secret — fallback), `Model`,
`CliPath` (points at `/usr/local/bin/claude-stub` in system tests). `MaxTurns` is honored
when present in stored settings but deliberately not offered in the editor.

## Structure

- `ClaudeCodeCliSlotHandler` — `ISlotHandler`; maps slot settings to options, registers
  `ICodingAgent`. Fails registration when neither `OAuthToken` nor `ApiKey` is present.
- `ClaudeCodeCliAgent` — process orchestration; `BuildStartInfo` is internal for unit tests.
- `ClaudeStreamJsonParser` — pure line-to-entries mapping + terminal result capture;
  tolerant of unknown event types and malformed lines.
- `IClaudeCliProcessFactory` / `IClaudeCliProcess` — the only OS-process seam; tests inject
  fakes, production uses `ClaudeCliProcessFactory`. The real process spawn is exercised by
  the system test through the in-container stub CLI.

## Special rules

- ANY exception leaving `RunAsync` — cancellation, a chat callback whose bus publish failed —
  kills the CLI's process tree (`IClaudeCliProcess.Kill`, no-op once exited) before rethrowing.
- The credential reaches the CLI ONLY via the child process environment
  (`CLAUDE_CODE_OAUTH_TOKEN` for a connected account, `ANTHROPIC_API_KEY` for the
  fallback key; only the one in use is exported) — never as a command-line argument
  (visible in `ps`), never in error messages, chat entries, or logs.
- `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` and `DISABLE_AUTOUPDATER=1` are part of the
  egress contract with the workflow's declared `api.anthropic.com` baseline — do not remove.
- Keep the parser lossless-but-bounded: entries are truncated at 4000 chars, unknown events
  are skipped silently (the CLI adds event types over time).
