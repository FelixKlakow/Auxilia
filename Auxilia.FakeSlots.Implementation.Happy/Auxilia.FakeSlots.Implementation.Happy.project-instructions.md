# Auxilia.FakeSlots.Implementation.Happy

Fake, test-only slot-handler plugin DLL that fills every slot of the implementation workflow
(`Auxilia.ImplementationWorkflow`) with in-memory fakes — the happy path: the implementation
agent returns "Implementation complete.", the fake test runner reports all tests passing, and the
PR opens. Registers provider type `fake-implementation-happy`.

Builds `Auxilia.FakeSlots.Implementation.Happy.slothandler.dll` (see `<AssemblyName>`) with its
`*.slothandler.manifest.json` sidecar — the `FileSystemPluginDiscovery` naming contract. Unsigned
(`Category: test-fake`, blank signature/hash, no `Settings`), so it loads only under
`AUXILIA_DEVELOPER_MODE=1`. `Auxilia.FakeSlots.Tests` publishes then resolves it; the
implementation system-test environment publishes it into the container plugins dir and binds each
slot to `fake-implementation-happy`.

## Special Rules

- `repository` is registered keyed to the slot as BOTH `ISourceControlWriteAccess` and
  `ISourceControlAccess` (one shared instance); `task-source` also registers a
  `WorkItemTrigger("WI-1")` singleton so a run has a triggering work item.

## File / Folder Map

```
Auxilia.FakeSlots.Implementation.Happy.csproj                      # net10.0 slothandler DLL; refs the implementation workflow + capability libs (incl. TestRunner)
ImplementationHappySlotHandler.cs                                  # ISlotHandler; per-slot canned happy fakes
Auxilia.FakeSlots.Implementation.Happy.slothandler.manifest.json   # provider fake-implementation-happy; contracts, test-fake category, no Settings
```
