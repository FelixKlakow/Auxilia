# Auxilia.FakeSlots.Implementation.AgentFailure

Fake, test-only slot-handler plugin DLL for the implementation workflow
(`Auxilia.ImplementationWorkflow`): wires the same slots as the happy fake, but the
`implementation-agent`'s `OpenSessionAsync` throws `Simulated agent failure` — it exercises the
agent-step failure path (the repository, test-runner, reviewer, and PR fakes are otherwise happy).
Registers provider type `fake-implementation-agent-failure`.

Builds `Auxilia.FakeSlots.Implementation.AgentFailure.slothandler.dll` (see `<AssemblyName>`) with
its `*.slothandler.manifest.json` sidecar — the `FileSystemPluginDiscovery` naming contract.
Unsigned (`Category: test-fake`, blank signature/hash, no `Settings`), so it loads only under
`AUXILIA_DEVELOPER_MODE=1`. Published and bound like the happy fake by `Auxilia.FakeSlots.Tests`
and by the implementation system-test environment, where it is the edge-case provider.

## Special Rules

- Only the `implementation-agent` slot fails; `reviewer-agent` still returns an empty session.
  `repository` is keyed as both `ISourceControlWriteAccess`/`ISourceControlAccess` and
  `task-source` registers a `WorkItemTrigger("WI-1")` singleton, matching the happy fake.

## File / Folder Map

```
Auxilia.FakeSlots.Implementation.AgentFailure.csproj                      # net10.0 slothandler DLL; refs the implementation workflow + capability libs (incl. TestRunner)
ImplementationAgentFailureSlotHandler.cs                                 # ISlotHandler; happy fakes except implementation-agent OpenSessionAsync throws
Auxilia.FakeSlots.Implementation.AgentFailure.slothandler.manifest.json  # provider fake-implementation-agent-failure; test-fake category, no Settings
```
