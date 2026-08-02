# Auxilia.Adapters.Email.Tests

Unit tests for `Auxilia.Adapters.Email` — the IMAP-polling email task-source adapter (`EmailTaskSourceAdapter`).

## Special Rules
Everything is exercised through hand-written fakes — no Moq, no network, no Docker: `RecordingBus` (`IMessageBusClient`), `FakeMailboxClientFactory`/`FakeMailboxClient`, `ManualTimeProvider`, and `InMemoryDataAccess<T>` for the trigger/instance/health/audit stores. A shared `_journal` list makes the publish-then-mark-seen ordering observable — the `\Seen` flag may only be set after the dispatch is on the bus.

## File / Folder Map
```
Tests/Libraries/Auxilia.Adapters.Email.Tests/
└── UnitTests/
    └── EmailTaskSourceAdapterTests.cs   # PollDueTriggersAsync: filter + dispatch to RunWorkflowCommand (by config id), per-trigger poll interval, disabled/deleted-instance skip, health sidecar streak + recovery, mark-seen ordering
```
