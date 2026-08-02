# Auxilia.FakeSlots.Tests

Behavioural tests for the four `Auxilia.FakeSlots.*` plugin projects (the fake slot handlers that workflow/system tests consume as fixtures) — proving they register the expected slots and simulate the scripted happy/failure behaviour. Slot-registration tests are uncategorized; plugin discovery/loading is `[Category("Integration")]`.

## Special Rules
- `PluginLoadingTests` shells out to `dotnet publish` for the four plugin projects into a temp dir, then drives `FileSystemPluginDiscovery`/`PluginLoader` under `AUXILIA_DEVELOPER_MODE=1`; it needs the .NET SDK and is slow — not part of the pre-commit Unit run.
- Slot handlers are exercised directly against a real `ServiceCollection`; assert keyed vs non-keyed registration exactly as the workflows resolve them.

## File / Folder Map
```
Tests/System/Auxilia.FakeSlots.Tests/
├── CodeReviewSlotHandlerTests.cs      # CodeReview Happy + WriteBackFailure: slot DI registration; reviewer AI records findings/verdicts; write-back throws "Simulated write-back failure"
├── ImplementationSlotHandlerTests.cs  # Implementation Happy + AgentFailure: per-slot DI registration; agent-failure OpenSession throws "Simulated agent failure"
└── PluginLoadingTests.cs              # [Integration] publish → discover → load all four plugin DLLs (developer mode)
```
