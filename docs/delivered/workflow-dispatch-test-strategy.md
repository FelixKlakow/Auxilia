# Workflow Dispatch – Test Strategy

> Status: **Draft** — implementation complete; tests not yet written.

This document records the test strategy for the workflow dispatch pipeline added in this iteration:
`RunWorkflowCommand` → `WorkflowDispatcher` → `IWorkflowLauncher` → Docker container → `WorkflowAnnouncementHandler` → `WorkflowDirective(Run)` → `WorkflowRegistrationHandler` → `WorkflowConfigurationResponse` → `WorkflowStateMessage`.

Cross-reference: `docs/TestStrategy.md` for tier definitions.

---

## What was built

| Component | Location | Responsibility |
|---|---|---|
| `RunWorkflowCommand` | `Source/Libraries/Auxilia.Workflows/Messaging/Messages/` | New message: external trigger → Core.Runner |
| `WorkflowDispatcher` | `Auxilia.Core.Runner/Workflows/` | Consumes `workflow.run-commands`; calls `IWorkflowLauncher` |
| `IWorkflowLauncher` | `Auxilia.Core.Runner/Workflows/` | Abstraction over "start a workflow process" |
| `WorkflowLaunchRequest` | `Auxilia.Core.Runner/Workflows/` | Launcher parameter: image + env vars |
| `DockerWorkflowLauncher` | `Auxilia.Core.Runner/Workflows/` | Production impl: `docker run -d` + injects RabbitMQ env vars |
| `DockerWorkflowLauncherSettings` | `Auxilia.Core.Runner/Workflows/` | Config POCO (`WorkflowLauncher` section) |
| `WorkflowAnnouncementHandler` | `Auxilia.Core.Runner/Workflows/` | Consumes `workflow.announcements`; responds `WorkflowDirective(Run)` |
| `IWorkflowBuilder.WithRunBody` | `Source/Libraries/Auxilia.Workflows/` | Registers business-logic delegate; executed inside resolved DI scope |
| `SlotConfigurationsSettings` | `Auxilia.Core.Runner/Workflows/Storage/` | Config-file slot config seeding |

---

## Level 1 – Unit Tests

**Project:** `Tests/Platform/Auxilia.Core.Runner.Tests/` (new unit test files)

### `WorkflowAnnouncementHandlerTests`

| Test | Setup | Assert |
|---|---|---|
| `WhenAnnouncementReceived_PublishesRunDirectiveToResponseTopic` | Strict mock `IMessageBusClient`; capture the subscription handler via `SubscribeAsync` setup; invoke handler manually with a `WorkflowAnnouncementMessage` | `PublishAsync` called once with `WorkflowDirective(Run)` on the exact `ResponseTopic` from the announcement |
| `WhenStartCalled_DeclaresAnnouncementsQueue` | Mock bus | `DeclareQueueAsync("workflow.announcements")` called once |

### `WorkflowDispatcherTests`

| Test | Setup | Assert |
|---|---|---|
| `WhenRunCommandReceived_LaunchesWorkflowWithCorrectImage` | Mock `IWorkflowLauncher`; capture `WorkflowLaunchRequest` | `Image` matches `command.WorkflowImage` |
| `WhenRunCommandReceived_InjectsRabbitMqEnvVars` | Mock launcher with settings seeded | Env vars `RabbitMq__Host`, `RabbitMq__Port`, `RabbitMq__UserName`, `RabbitMq__Password` all present |
| `WhenRunCommandHasContext_ForwardsContextAsEnvVars` | Command with `Context = { "REPO_URL": "..." }` | `WORKFLOW_CONTEXT__REPO_URL` env var present |
| `WhenStartCalled_DeclaresRunCommandsQueue` | Mock bus | `DeclareQueueAsync("workflow.run-commands")` called once |

### `DockerWorkflowLauncherEnvVarTests` *(pure logic, no Docker)*

These tests verify the argument-building logic of `DockerWorkflowLauncher` is correct before touching Docker.

| Test | What to verify |
|---|---|
| `WhenNetworkNameConfigured_NetworkFlagPresent` | The `--network <name>` args appear in the process invocation |
| `WhenNetworkNameNull_NetworkFlagAbsent` | No `--network` arg |
| `WhenEnvVarsContainSpecialChars_ArgListEscapesCorrectly` | Values with spaces/`=` are passed as single`ArgumentList` entries, not injected into a shell string |

> Note: these tests require extracting the argument-building into a pure, testable helper method. Plan to refactor `DockerWorkflowLauncher` to have an `internal BuildArgs(WorkflowLaunchRequest, DockerWorkflowLauncherSettings) → IReadOnlyList<string>` method before writing the tests.

### `WorkflowRegistrationHandlerNoSlotTests`

| Test | Assert |
|---|---|
| `WhenManifestHasNoSlots_SucceedsWithoutConsultingConfigResolver` | `ConfigurationResolver.Resolve` is never called; response `Success=true`, `Slots` is empty |

---

## Level 2 – Component Tests

**Project:** `Tests/Platform/Auxilia.Core.Runner.Tests/ComponentTests/` (new folder)

Use `FakeMessageBusClient` (from `Auxilia.Messaging`) and a `FakeWorkflowLauncher` (to be created — records calls, does not invoke Docker).

### `WorkflowDispatchPipelineComponentTests`

End-to-end through the in-process DI container of the Core.Runner; no real network calls.

| Test | Steps | Assert |
|---|---|---|
| `WhenRunCommandPublished_WorkflowLauncherIsCalled` | Build host with `FakeMessageBusClient`; publish `RunWorkflowCommand` on `workflow.run-commands` | `FakeWorkflowLauncher.Calls.Count == 1` within 5 s |
| `WhenWorkflowAnnounces_RunDirectiveIsPublished` | Publish `WorkflowAnnouncementMessage` on `workflow.announcements` | A `WorkflowDirective(Run)` is published to the `ResponseTopic` within 5 s |
| `WhenNoSlotWorkflowRegisters_ConfigurationResponseIsSuccess` | Simulate the full announce → directive → register flow for a workflow with `Slots.Count == 0` | `WorkflowConfigurationResponse.Success == true` and `Slots` is empty |
| `WhenSlotConfigsSeededFromSettings_ConfigurationResponseContainsSlot` | Seed `SlotConfigurationsSettings` with one entry; simulate full flow | `WorkflowConfigurationResponse.Slots` contains the expected slot name |

---

## Level 3 – System Test

**Project:** `Tests/System/Auxilia.SystemTestSuite/`

**Status:** Strategy defined here; implementation tracked separately.

### New environment: `WorkflowDispatchEnvironment`

**File:** `Tests/System/Auxilia.SystemTestSuite/WorkflowDispatch/WorkflowDispatchEnvironment.cs`

**Containers:**

| Container | Image | Notes |
|---|---|---|
| RabbitMQ | `rabbitmq:3.13-management` | Internal alias `rabbitmq` |
| MongoDB | `mongo:7` | Required by Core.Runner in future; included now for parity |
| Core.Runner | `auxilia-core-runner:system-test` | Built from source; `WorkflowLauncher:NetworkName` set to the Testcontainers network name; `SlotConfigurations` seeded with a `"local-git"` entry |
| SimpleGitWorkflow | `auxilia-simple-git-workflow:system-test` | **Not pre-started** — launched on demand by Core.Runner's `DockerWorkflowLauncher` |
| Git server | `alpine/git` (init bare) or a bind-mounted temp dir | Acts as the target repository for the workflow |

**Key setup considerations:**
- The Testcontainers network name must be passed to the Core.Runner via `WorkflowLauncher:NetworkName` so that any workflow containers it spawns are on the same network.
- The bare git repo can be a bind-mounted host directory (created in `[OneTimeSetUp]` via `git init`); the path is shared into all containers that need it.
- The Core.Runner must be configured with a slot config for `"simple-git-workflow"` pointing at the repo path inside the container.
- Core.Runner's startup log message `"WorkflowDispatcher started"` should be used as the `UntilMessageIsLogged` wait strategy.

### Test class: `WorkflowDispatchSystemTests`

**File:** `Tests/System/Auxilia.SystemTestSuite/WorkflowDispatch/WorkflowDispatchSystemTests.cs`

```
[TestFixture]
[Category("System")]
```

| Test | Steps | Assert |
|---|---|---|
| `WhenSimpleGitWorkflowTriggered_CommitAppearsInRepository` | 1. Subscribe to `workflow.state` on the shared `IMessageBusClient`. 2. Publish `RunWorkflowCommand(WorkflowType="simple-git-workflow", WorkflowImage="auxilia-simple-git-workflow:system-test", Context={})` to `workflow.run-commands`. 3. `WaitAsync` up to 60 s for a `WorkflowStateMessage`. | `WorkflowStateMessage.State == WorkflowState.Success`; running `git log` on the test repo shows a new commit with a message containing `"test.txt"` |

---

## New project: `Auxilia.SimpleGitWorkflow`

> Required by the Level 3 test. Not yet implemented.

A minimal `.csproj` (console app) whose `Program.cs` is:

```csharp
await WorkflowBuilder.Create("simple-git-workflow")
    .RequiresSourceControl("source-control",
        new SourceControlCapabilities { RequiredPermissions = [Permission.Write] },
        description: "Target repository for the smoke test commit.")
    .WithRunBody(async (sp, ct) =>
    {
        var repoPath = System.Environment.GetEnvironmentVariable("WORKFLOW_CONTEXT__REPO_PATH")
                       ?? throw new InvalidOperationException("WORKFLOW_CONTEXT__REPO_PATH not set.");

        // Use LibGit2Sharp (or CLI) to:
        //   1. Open the repo at repoPath
        //   2. Write "hello from Auxilia system test" to test.txt
        //   3. Stage and commit with message "chore: system test commit"
    })
    .Run(args);
```

**Slot config** the Core.Runner needs for this workflow:
```json
"SlotConfigurations": {
  "Workflows": {
    "simple-git-workflow": [
      {
        "SlotName": "source-control",
        "ProviderType": "LocalGit",
        "Settings": { "RepositoryPath": "/repos/test-repo" }
      }
    ]
  }
}
```

The `"LocalGit"` slot handler does not yet exist — it is the next piece to build to make the Level 3 test fully runnable.

---

## Summary: what remains before the Level 3 test can be implemented

| # | Task |
|---|---|
| 1 | Create `Auxilia.SimpleGitWorkflow` project + Dockerfile |
| 2 | Create `LocalGit` slot handler (reads `RepositoryPath` from settings; mounts it for workflow use) |
| 3 | Implement `WithRunBody` logic in `SimpleGitWorkflow` (LibGit2Sharp or `git` CLI) |
| 4 | Implement `WorkflowDispatchEnvironment` (SetUpFixture) following the strategy above |
| 5 | Implement `WorkflowDispatchSystemTests` |
| 6 | Add `FakeWorkflowLauncher` to the Core.Runner test project for Level 2 use |

