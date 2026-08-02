# Auxilia.ClaudeCode.Workflow.Tests

Unit and component tests for `Source/Workflows/Auxilia.ClaudeCode.Workflow` (the one-shot Claude-Code coding workflow).

## Special Rules
- The component test drives the whole workflow through `Auxilia.Workflows.Testing.WorkflowTestHarness`; the dispatch context arrives via the `WORKFLOW_CONTEXT__TITLE` / `WORKFLOW_CONTEXT__BODY` env vars and the output dir via `WorkflowEnvironmentVariables.OutputDirectory` (all reset in `TearDown`).
- A scripted `ICodingAgent` is injected by setting `WorkflowBuilder.TestSlotHandlerResolver`; clear it in `TearDown`.

## File / Folder Map
```
Tests/Workflows/Auxilia.ClaudeCode.Workflow.Tests/
├── UnitTests/
│   ├── ClaudeCodeRunContextTests.cs   # title+body → single instruction; missing-instruction throws; temp-dir fallbacks
│   ├── SessionReportWriterTests.cs    # SessionReport JSON round-trip to session-report.json
│   └── ClaudeCodeApplicationTests.cs  # mocked ICodingAgent + IViewPublisher: chat/progress views, report always written (even on failure, then rethrows)
└── ComponentTests/
    └── ClaudeCodeWorkflowScenarioTests.cs  # full workflow via harness + scripted ICodingAgent slot; success and failure both write the report
```
