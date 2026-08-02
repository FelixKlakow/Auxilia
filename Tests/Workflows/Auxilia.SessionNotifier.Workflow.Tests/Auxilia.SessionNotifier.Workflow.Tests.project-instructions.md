# Auxilia.SessionNotifier.Workflow.Tests

Unit tests (`Category=Unit`) for `Source/Workflows/Auxilia.SessionNotifier.Workflow` (posts a coding-session outcome back onto the originating work item).

## Special Rules
- No harness: `IWorkItemAccess` is mocked with `MockBehavior.Strict`; each test writes the result artifact to a temp JSON file.

## File / Folder Map
```
Tests/Workflows/Auxilia.SessionNotifier.Workflow.Tests/
└── NotifierApplicationTests.cs   # reads a materialized SessionSummary or AgentSessionReport artifact and posts a formatted comment via IWorkItemAccess; no work item → completes quietly; missing artifact → throws; FormatSummary/FormatReport cover timeout, failed-run error, and absent figures
```
