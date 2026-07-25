# Auxilia.ImplementationWorkflow.Tests

Tests for `Source/Auxilia.ImplementationWorkflow` (`ImplementationWorkflow`): whole-workflow scenarios under `Scenarios/` (`Category=Component`) plus uncategorized `[TestFixture]` unit-level fixtures for the reviewer orchestrator and the MCP result-sink.

## Special Rules
- Mixed categorization: only `Scenarios/` carries `[Category("Component")]`; the root fixtures have no `Category`, so `--filter "Category=Unit"` does **not** run them — they run only in a full pass.
- `ScenarioTestBase.RunScenarioAsync` runs the real workflow through `WorkflowTestHarness` wired from `ImplementationFakeRegistry` (6 slots: repository, task-source, implementation-agent, reviewer-agent, test-runner, pull-request).
- `FakeAiAgent` scripts multi-session transcripts (`Queue<IReadOnlyList<ScriptedTurn>>`); reviewer notes are recorded via the `ImplementationReviewResultSinkMcpTools` sink, not returned as text.
- The repository slot handler also injects `ISignalEmitter` (`FakeSignalEmitter`), the config, and the `WorkItemTrigger`; tool-policy denials are simulated with `FakeSourceControlWriteAccess.PolicyDenyList`.

## File / Folder Map
```
Auxilia.ImplementationWorkflow.Tests/
├── Scenarios/                       # Component: full ImplementationWorkflow runs via WorkflowTestHarness
│   ├── ScenarioTestBase.cs          # harness driver; DefaultRegistry + default implementer/reviewer agent factories
│   ├── HappyPathTests.cs            # success → branch, PR-link comment, implementation-summary.json, Completed signal
│   ├── ReviewerEnabledTests.cs, ReviewerDisabledTests.cs, ReviewerFlagsIssuesTests.cs  # optional reviewer pass
│   ├── AgentFailureTests.cs, WriteBackFailureTests.cs, TestRunnerAsyncFailureTests.cs  # failure paths
│   └── ToolPolicyEnforcementTests.cs  # denied SourceControlOperation → Failed state + Failed signal
├── Fakes/
│   ├── ImplementationFakeRegistry.cs  # registers the 6 slots + signal emitter / config / WorkItemTrigger
│   ├── FakeSourceControlWriteAccess.cs, FakeTaskSourceAccess.cs, FakeTestRunner.cs, FakePullRequestAccess.cs (+ matching *SlotHandler.cs)
│   ├── FakeAiAgent.cs, FakeAiSession.cs, ScriptedTurn.cs  # multi-session scripted AI turns
│   └── FakeSignalEmitter.cs           # captures emitted workflow signals
├── ReviewerOrchestratorTests.cs     # reviewer enable/disable; note recording + severity via the sink
└── Mcp/ImplementationReviewResultSinkMcpToolsTests.cs  # review-note sink accumulate/drain, thread-safety, no enum coercion
```
