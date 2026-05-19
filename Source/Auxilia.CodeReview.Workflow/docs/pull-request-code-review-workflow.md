# Pull-Request Code-Review Workflow — Operator and Integrator Reference

This document is written for **operators** configuring the workflow and **integrators** building capability plugins. For the internal implementation reference (phase diagram, output artifact schemas, slot capability records), see [`WORKFLOW.md`](WORKFLOW.md) in the same directory.

---

## 1 — Workflow Overview

The pull-request code-review workflow performs a multi-pass AI-assisted review of a pull request's changed files. It is provider-neutral: every external dependency (source control, PR host, task tracker, AI) is supplied through named slots. No concrete SDK, API client, or cloud provider is referenced by the workflow itself.

### Trigger

The workflow entry point accepts a `PullRequestReference` input record:

```csharp
public sealed record PullRequestReference(string PrIdentifier, string BaseRef, string HeadRef);
```

| Field | Meaning |
|---|---|
| `PrIdentifier` | Provider-assigned identifier for the pull request (e.g. `"42"`, `"PR-2001"`) |
| `BaseRef` | Git ref of the target branch (e.g. `"main"`) |
| `HeadRef` | Git ref of the PR branch (e.g. `"feature/my-change"`) |

### Output Artifact

The workflow writes a `CodeReviewResult` record to the declared output path (`output/code-review-result.json`). The record contains the PR identifier, a list of `ReviewFinding` items, and a plain-text summary.

Coverage statistics are captured in a `CoverageMetrics` record (written to `output/coverage-metrics.json`) that tracks total files, reviewed / skipped / failed counts, staged-finding count, and surviving-finding count.

### Provider Neutrality

The workflow never imports any AI SDK, SCM client, or issue-tracker library. All I/O is performed through the five named slot interfaces described in Section 2.

---

## 2 — Named Slots

Each slot declares a required capability type and a slot name that must be supplied at configuration time.

| Slot Name | Capability Interface | Description |
|---|---|---|
| `repository` | `ISourceControlAccess` | Read-only access to the Workspace-Manager-mounted repository snapshot. Provides file listing, file content reading, and changed-file enumeration via git history. |
| `pull-request` | `IPullRequestAccess` | Read-write access to the PR host. Provides the changed-file inventory, per-file diff hunks, existing review comments, linked work-item references, and the ability to post new review comments. |
| `work-items` | `IWorkItemAccess` | Access to the team's task tracker. Provides single and batch work-item lookup and optional comment posting. Failure behavior during context assembly is configurable (see Section 3). |
| `primary-reviewer` | `IAiAgent` | AI agent that performs the per-file review pass. Registered as a keyed singleton under the slot name `"primary-reviewer"`. Requires a minimum context window of 128 000 tokens and text-modality support. |
| `secondary-reviewer` | `IAiAgent` | AI agent used for the two-eyes validation pass. Registered as a keyed singleton under the slot name `"secondary-reviewer"`. Only active when `TwoEyesConfiguration.Enabled = true`. Has the same capability requirements as `primary-reviewer`. |

The slot names `"primary-reviewer"` and `"secondary-reviewer"` are hard-coded in the workflow. They are not configuration values.

---

## 3 — Configuration Surface

### `TwoEyesConfiguration`

Controls whether each staged finding from the primary review pass is independently validated by the secondary reviewer before inclusion in the final result.

| Property | Type | Default | Effect |
|---|---|---|---|
| `Enabled` | `bool` | `false` | When `true`, the `secondary-reviewer` slot is activated; each staged finding receives an `Approved` or `Rejected` secondary verdict. When `false`, all staged findings are included with `SecondaryVerdict.NotReviewed`. |

### `WriteBackConfiguration`

Controls how surviving findings are written back to the PR host and linked work items.

| Property | Type | Default | Effect |
|---|---|---|---|
| `MinimumSeverity` | `FindingSeverity` | `Info` | Findings whose severity falls below this threshold are not posted as PR review comments. Severity order (highest to lowest): `Critical > High > Medium > Low > Info`. |
| `PostSummaryToWorkItems` | `bool` | `false` | When `true`, a summary comment is posted to each linked work item via `IWorkItemAccess.PostCommentAsync` after findings have been written back to the PR. |

### `WorkItemRetrievalFailureBehavior`

Determines how the workflow responds when `IWorkItemAccess.GetWorkItemsAsync` throws during context assembly.

| Value | Effect |
|---|---|
| `Ignore` | The exception is swallowed; `ReviewContext.LinkedWorkItems` is set to an empty list and the run continues. |
| `Fail` | The exception propagates from `ContextAssembler.AssembleAsync`, aborting the workflow run. |

### Criticality Classification Rules

`CriticalityClassifier` accepts a list of glob patterns at configuration time. A changed file whose relative path matches any pattern in the list is classified as `FileCriticality.Critical`. All other files are `FileCriticality.Normal`.

Critical files that the primary reviewer marks `Skipped` are automatically reclassified as `FileVerdict.Failed`; they contribute to the failed-file count in `CoverageMetrics`.

| Pattern list | Effect |
|---|---|
| _(empty)_ | Every file is `Normal`. No file can become `Critical`. |
| `**/*.sql` | Any SQL file in the changed set is `Critical`. |
| `src/core/**` | Any file under `src/core/` is `Critical`. |

---

## 4 — MCP Tool Mapping

Each capability library ships its own MCP tool class. Workflow code never defines tools directly. Tool names follow the pattern `{slotName}.{toolName}`, where `{slotName}` is the name used when configuring the slot. This prefix prevents name collisions when multiple slots are active simultaneously in the same MCP server.

### `ISourceControlAccess` — slot `repository`

Implemented by `SourceControlAccessMcpTools`.

| Interface method | Signature | MCP tool name |
|---|---|---|
| `ListFilesAsync` | `(string? relativePath, CancellationToken)` | `repository.list_files` |
| `ReadFileContentAsync` | `(string relativePath, CancellationToken)` | `repository.read_file` |
| `GetChangedFilesAsync` | `(string baseRef, string headRef, CancellationToken)` | `repository.get_changed_files` |

A read-only MCP resource (`scm://working-path`) exposes the `WorkingPath` property of the mounted repository.

### `IPullRequestAccess` — slot `pull-request`

Implemented by `PullRequestAccessMcpTools`.

| Interface method | Signature | MCP tool name |
|---|---|---|
| `GetChangedFilesAsync` | `(CancellationToken)` | `pull-request.get_changed_files` |
| `GetDiffHunksAsync` | `(string filePath, CancellationToken)` | `pull-request.get_diff_hunks` |
| `GetCommentsAsync` | `(CancellationToken)` | `pull-request.get_comments` |
| `GetLinkedWorkItemsAsync` | `(CancellationToken)` | `pull-request.get_linked_work_items` |
| `PostCommentAsync` | `(string body, string? filePath, int? lineNumber, CancellationToken)` | `pull-request.post_comment` |

> **Note:** Both `ISourceControlAccess` and `IPullRequestAccess` expose a `get_changed_files` operation, but they are on different slots (`repository.get_changed_files` and `pull-request.get_changed_files`). The slot-name prefix ensures these are distinct MCP tools.

### `IWorkItemAccess` — slot `work-items`

Implemented by `WorkItemAccessMcpTools`.

| Interface method | Signature | MCP tool name |
|---|---|---|
| `GetWorkItemAsync` | `(string id, CancellationToken)` | `work-items.get_work_item` |
| `GetWorkItemsAsync` | `(IEnumerable<string> ids, CancellationToken)` | `work-items.get_work_items` |

`PostCommentAsync` is not exposed as an MCP tool; it is called directly by `WriteBackService` when `WriteBackConfiguration.PostSummaryToWorkItems = true`.

### `IAiAgent` — slots `primary-reviewer` and `secondary-reviewer`

Implemented by `AiInferenceMcpTools`.

| Interface method | Signature | MCP tool name |
|---|---|---|
| `OpenSessionAsync` | `(AiSessionOptions? options, CancellationToken)` | _(internal — not directly exposed as a tool)_ |

The MCP tool surface wraps `OpenSessionAsync` followed by `IAiSession.ExecuteAsync` in a single round-trip:

| Operation | MCP tool name |
|---|---|
| Open session + execute prompt | `primary-reviewer.run_inference` |
| Open session + execute prompt | `secondary-reviewer.run_inference` |

Each call to the `run_inference` tool opens a fresh `IAiSession` via `IAiAgent.OpenSessionAsync`, sends the prompt via `IAiSession.ExecuteAsync`, disposes the session, and returns the plain-text response.

---

## 5 — Phase Narrative

The five workflow phases map to the steps a human code reviewer would take.

### Phase 1 — Workflow Registration

Before any review work starts, the workflow declares what external systems it needs. Five named slots are registered (one for each capability abstraction) and an output descriptor is created. This phase is analogous to a reviewer ensuring they have access to the codebase, the PR, the project backlog, and their review guidelines before opening the first file.

### Phase 2 — Context Assembly

The workflow reads everything it needs to form an opinion. The Workspace Manager has mounted a full copy-on-write snapshot of the repository at a local path; `ISourceControlAccess.WorkingPath` exposes that mount point. Changed files and per-file diff hunks are fetched from the PR host via `IPullRequestAccess`. Work items linked to the PR are batch-fetched via `IWorkItemAccess`; if retrieval fails, behaviour is governed by `WorkItemRetrievalFailureBehavior`. Each changed file is then classified as `Normal` or `Critical` according to the configured glob patterns. The result is an immutable `ReviewContext` snapshot that all subsequent phases read from but never modify.

### Phase 3 — Primary Review Pass

The workflow opens a single `IAiSession` for the `primary-reviewer` slot and iterates every `ReviewableFile` in the `ReviewContext`. For each file the diff hunks are sent to the session. The session responds with a verdict (`Reviewed`, `Skipped`, or `Failed`) and any findings. Findings are held in memory in an `IStagedFindingsStore`; they are never written to the PR host at this stage. If the session's accumulated token usage exceeds the configured threshold, the `ContextCompactionService` summarises the session, disposes it, opens a fresh session, and injects a compact summary — preserving continuity without exceeding the model's context window. A `Critical` file that receives a `Skipped` verdict is automatically escalated to `FileVerdict.Failed`.

### Phase 4 — Two-Eyes Pass and Aggregation

When `TwoEyesConfiguration.Enabled = false`, every staged finding is wrapped directly into the final result with `SecondaryVerdict.NotReviewed`. When enabled, the `TwoEyesPassService` opens one `IAiSession` per finding for the `secondary-reviewer` slot and asks it to approve or reject. Rejected findings are discarded. The `FindingsAggregator` then collects all `Approved` and `NotReviewed` findings and assembles the `CodeReviewResult`.

### Phase 5 — Write-Back and Terminal Metrics

With the final finding list assembled, the `WriteBackService` posts each finding that meets or exceeds `WriteBackConfiguration.MinimumSeverity` as a review comment on the PR via `IPullRequestAccess.PostCommentAsync`. If `WriteBackConfiguration.PostSummaryToWorkItems = true`, a summary comment is also posted to each linked work item. The `CoverageMetricsProducer` then computes file-level and finding-level metrics across the entire run. Finally, `TerminalStatePublisher` serialises and writes `code-review-result.json` and `coverage-metrics.json` to the declared output path.
