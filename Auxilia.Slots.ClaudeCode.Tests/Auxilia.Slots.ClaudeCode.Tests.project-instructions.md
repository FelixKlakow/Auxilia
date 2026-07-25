# Auxilia.Slots.ClaudeCode.Tests

Unit tests for `Auxilia.Slots.ClaudeCode` — the Claude Code CLI coding-agent slot (handler, agent process wrapper, and stream-json parser).

## Special Rules
`ClaudeCodeCliAgent` is tested without spawning the real `claude` binary: a hand-written `IClaudeCliProcessFactory`/`IClaudeCliProcess` fake (`FakeProcessFactory`/`FakeProcess`) feeds canned stream-json stdout/stderr and an exit code, and `BuildStartInfo` is asserted directly. Credential invariant under test — API key / OAuth token are passed only via process environment, never on the command line.

## File / Folder Map
```
Auxilia.Slots.ClaudeCode.Tests/
└── UnitTests/
    ├── ClaudeCodeCliSlotHandlerTests.cs  # DI registration → ICodingAgent/ClaudeCodeCliAgent; ClaudeCodeCliOptions.FromSettings defaults + credential precedence; unknown-slot / missing-credential throws
    ├── ClaudeCodeCliAgentTests.cs         # BuildStartInfo args + env; RunAsync streaming over FakeProcess; success/error/no-result outcomes; cancellation kills the process
    └── ClaudeStreamJsonParserTests.cs     # ParseLine: stream-json events → AgentChatEntry; tool_use/tool_result pairing; oversized-content truncation; malformed lines ignored
```
