# Auxilia.CodeReview.Workflow.Tests

Tests for `Source/Workflows/Auxilia.CodeReview.Workflow` (`PullRequestReviewWorkflow`): whole-workflow scenarios under `Scenarios/` (`Category=Component`) plus uncategorized `[TestFixture]` unit-level fixtures for the individual services/orchestrators.

## Special Rules
- Mixed categorization: only `Scenarios/` carries `[Category("Component")]`; the root/subfolder service fixtures have no `Category`, so `--filter "Category=Unit"` does **not** run them — they run only in a full pass.
- Scenarios run the real workflow through `WorkflowTestHarness`; AI behaviour is scripted by having `ScriptedTurn.ToolCall` write verdicts/findings into the `CodeReviewResultSinkMcpTools` result-sink (never by returning text — see CLAUDE.md AI rule).
- `WorkflowBuilder.TestSlotHandlerResolver` is the injection seam; `ScenarioTestBase.TearDown` clears it.

## File / Folder Map
```
Tests/Workflows/Auxilia.CodeReview.Workflow.Tests/
├── Scenarios/                       # Component: full PullRequestReviewWorkflow runs via WorkflowTestHarness
│   ├── ScenarioTestBase.cs          # harness driver; Reviewed/Skipped/Approved/Rejected ScriptedTurn factories; slot wiring
│   ├── HappyPathTests.cs, EmptyPrTests.cs, LinkedWorkItemTests.cs, WorkItemSummaryTests.cs
│   ├── TwoEyesTests.cs, TwoEyesPassTests.cs, PrimarySecondaryDisagreementTests.cs   # secondary-review verdict flows
│   ├── CriticalFilesTests.cs, CriticalFileCoverageTests.cs, EveryFileCoveredTests.cs # coverage / criticality
│   ├── ContextCompactionTests.cs                                                     # mid-review compaction
│   ├── WriteBackFilteringTests.cs, WriteBackFailureTests.cs                          # PR / work-item write-back
│   └── AiProviderFailureTests.cs                                                     # AI failure handling
├── Fakes/
│   ├── CodeReviewFakeRegistry.cs    # registers the 6 slots (repository, pull-request, work-items, primary/secondary AI, workflow-bootstrap)
│   ├── FakeSourceControlAccess.cs, FakePullRequestAccess.cs, FakeWorkItemAccess.cs (+ matching *SlotHandler.cs)
│   ├── FakeAiAgent.cs, FakeAiSession.cs, ScriptedTurn.cs   # scripted single-session AI feeding the result-sink
│   ├── FakeWorkflowBootstrapSlotHandler.cs                 # injects output dir + two-eyes/write-back/critical/compaction config
│   └── FakeInfrastructureTests.cs                          # tests OF the fakes themselves
├── PrimaryReview/PrimaryReviewOrchestratorTests.cs  # per-file verdicts, finding staging, one-session-per-run compaction
├── Findings/StagedFindingsStoreTests.cs             # append + point-in-time snapshot store
├── Compaction/ContextCompactionServiceTests.cs      # ShouldCompact threshold; forwards CompactAsync to the session
├── Mcp/CodeReviewResultSinkMcpToolsTests.cs         # result-sink accumulate/drain, thread-safety, no enum coercion
├── FindingsAggregatorTests.cs       # two-eyes filtering + primary/secondary attribution
├── TwoEyesPassServiceTests.cs       # secondary pass enable/disable; absent tool call defaults to Approved
├── CoverageMetricsProducerTests.cs  # coverage-metrics arithmetic
├── WriteBackServiceTests.cs         # severity thresholding + per-work-item summary
└── Phase5IntegrationTests.cs        # full-lifecycle harness run + TerminalStatePublisher writes code-review-result.json
```
