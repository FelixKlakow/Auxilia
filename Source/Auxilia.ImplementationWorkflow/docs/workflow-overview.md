# Implementation Workflow — Overview

## What It Does

The `Auxilia.ImplementationWorkflow` receives a work-item reference, checks out a new branch, runs a fully-autonomous AI implementation agent equipped with source-control, test-runner, and task-source tools, optionally runs a read-only reviewer pass, opens a pull request, writes back to the task source, persists an artifact summary, and emits output signals.

---

## Trigger

The workflow is triggered by a `WorkItemReference` via the `workflow-config` slot's `WorkItemId` setting. The `WorkItemTrigger` record is populated from this setting and injected into the DI container before `AddImplementationWorkflow` is called.

---

## Configuration Surface

The `ImplementationWorkflowConfiguration` record exposes the following settings:

| Property | Type | Default | Description |
|---|---|---|---|
| `WriteBackBehavior` | `Warn` \| `Fail` | `Warn` | Controls what happens when `PostCommentAsync` or `UpdateStatusAsync` throws. `Warn` logs and continues; `Fail` propagates the exception. |
| `ReviewerEnabled` | `bool` | `false` | When `true`, a second read-only AI agent reviews the implementation after the PR is opened and flags issues via the `ReviewNotesFlagged` signal. |
| `OutputDirectory` | `string` | `output` | Directory where `implementation-summary.json` is written. |

---

## Output Signals

### `Completed`
Emitted after write-back and artifact persistence succeed.

| Field | Type | Description |
|---|---|---|
| `PrUrl` | `string` | URL of the opened pull request |
| `BranchName` | `string` | Name of the implementation branch |
| `WorkItemId` | `string` | Work item that was implemented |

### `ReviewNotesFlagged`
Emitted (before `Completed`) when `ReviewerEnabled = true` and the reviewer agent returns at least one issue.

| Field | Type | Description |
|---|---|---|
| `PrUrl` | `string` | URL of the opened pull request |
| `BranchName` | `string` | Name of the implementation branch |
| `WorkItemId` | `string` | Work item that was implemented |
| `ReviewNotes` | `ReviewNoteDto[]` | Array of flagged issues (`Description`, `FilePath?`, `Severity`) |

### `Failed`
Emitted when the implementation agent throws an exception during its session.

| Field | Type | Description |
|---|---|---|
| `FailureReason` | `string` | Exception message |
| `WorkItemId` | `string` | Work item that failed |
| `PartialBranchName` | `string?` | Branch name if it was created before failure |

---

## Output Artifact: `implementation-summary.json`

Written to `{OutputDirectory}/implementation-summary.json`.

```json
{
  "workItemId": "WI-1",
  "branchName": "impl/WI-1-add-feature",
  "prUrl": "https://example.com/pr/42",
  "reviewNotes": [
    {
      "description": "Potential null reference in handler",
      "filePath": "src/Handler.cs",
      "severity": "warning"
    }
  ]
}
```

`reviewNotes` is an empty array when `ReviewerEnabled = false` or the reviewer found no issues.

---

## Dependency Blueprints

- **BP/20** — Signal handler configuration: describes how workflow hosts register signal handlers for `Completed`, `ReviewNotesFlagged`, and `Failed` events.
- **BP/21** — Capability interface and tool policy configuration: describes the `PolicyGuardedSourceControlWriteAccess` decorator and how to configure the allow/deny policy in `SlotConfiguration.Settings`.
