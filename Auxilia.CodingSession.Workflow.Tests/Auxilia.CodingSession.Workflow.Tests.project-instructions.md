# Auxilia.CodingSession.Workflow.Tests

Unit tests (all `Category=Unit`) for `Source/Auxilia.CodingSession.Workflow` (the interactive tmux/ttyd coding-session workflow).

## Special Rules
- No shared harness: fakes are private nested classes (`FakeSessionHost`, `FakeGitRunner`, `RecordingViews`); the application under test is constructed directly.

## File / Folder Map
```
Auxilia.CodingSession.Workflow.Tests/
├── SessionRunContextTests.cs        # FromValues defaults (claude, port 7681, 240 min); branch derived from instance id; temp-workspace fallback
├── CodingSessionApplicationTests.cs # branch → wait → collect changed files → write CodingSessionResult; mail-attachment materialization strips path traversal; max-duration timeout
└── SessionCredentialTests.cs        # credential→env mapping (OAuth token wins over API key); tmux start info carries the secret only in the environment, never on the command line
```
