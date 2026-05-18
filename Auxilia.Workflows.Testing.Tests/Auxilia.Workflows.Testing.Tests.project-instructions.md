# Auxilia.Workflows.Testing.Tests

Component tests for `Auxilia.Workflows.Testing`. Exercises the `WorkflowTestHarness` end-to-end using a minimal `TestWorkflow` entry point (a single `db` slot, `NoCapabilities`).

## File / Folder Map
```
Auxilia.Workflows.Testing.Tests/
├── TestWorkflow.cs             # Shared entry point: WorkflowBuilder "test-workflow" + one "db" slot
├── RunSuccessTests.cs          # Run directive + all slots configured → HarnessResult.State == Success
├── RunFailedConfigTests.cs     # Run directive + no slots configured → HarnessResult.State == Failed
├── EmitSchemaTests.cs          # EmitSchema directive → HarnessResult.Schema populated with correct metadata
└── TimeoutTests.cs             # Entry point that never announces → WorkflowHarnessTimeoutException thrown
```
