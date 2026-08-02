# Auxilia.Workflows.TestRunner

Workflow capability library for running a project's test suite: declares the `ITestRunner`
capability a workflow requires and ships the capability record, the builder extension, the
policy-guard decorator, and the MCP tool server. The concrete runner is supplied by a separate
slot package.

## Special Rules

- `PolicyGuardedTestRunner` gates `RunTestsAsync` on
  `IToolPolicy.IsAllowed(TestRunnerOperation.RunTests)` and throws `ToolPolicyDeniedException`
  when denied — it is the enforcement point; the raw runner is never handed to a workflow.
- `TestRunnerCapabilities.Extensions` (`[JsonExtensionData]`) is an opaque forward-compat
  passthrough — never read it for capability logic.
- The MCP tool name is slot-prefixed via `SlotMcpPrefix.Format`.

## File / Folder Map
```
Source/Libraries/Auxilia.Workflows.TestRunner/
├── ITestRunner.cs                          # runtime contract: RunTestsAsync(TestRunRequest)
├── TestRunRequest.cs                       # command + optional test filter / timeout
├── TestRunResult.cs                        # passed flag, exit code, pass/fail/skip counts, log output
├── TestRunnerCapabilities.cs               # ICapability (empty but for forward-compat Extensions)
├── TestRunnerOperation.cs                  # policy-keyed operation enum (RunTests)
├── TestRunnerWorkflowBuilderExtensions.cs  # RequiresTestRunner() over builder.Requires<T>
├── PolicyGuardedTestRunner.cs              # IToolPolicy decorator around ITestRunner
└── Mcp/TestRunnerMcpTools.cs               # MCP tool server exposing run_tests to AI agents
```
