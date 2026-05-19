# Pull-Request Code-Review Workflow

## Purpose

The pull-request code-review workflow performs an automated, multi-pass AI-assisted review of a pull request's changed files. It assembles full diff context, runs a primary AI review pass over every changed file, optionally validates findings through a secondary independent reviewer (two-eyes pass), aggregates surviving findings, and writes them back as review comments on the PR and optionally as work-item notes.

Use this workflow when you want a consistent, configurable code-review process driven by AI inference that integrates with your source-control host, task-tracker, and AI provider through the Auxilia slot mechanism.

---

## Named Slots

| Slot name | Capability record | Required capabilities |
|---|---|---|
| `repository` | `SourceControlCapabilities` | `RequiredPermissions = [Permission.Read]` — read-only access to the mounted repository snapshot |
| `pull-request` | `PullRequestAccessCapabilities` | `RequiredPermissions = ["ReadWrite"]` — read changed files, diff hunks, linked work-item refs; post review comments |
| `work-items` | `TaskSourceCapabilities` | `SupportedItemTypes = [UserStory, Bug, Feature, Epic]` — batch-fetch linked work items; optionally post summary comments |
| `primary-reviewer` | `AiCapabilities` | `MinContextWindow = 128 000`, `SupportedModalities = [Text]` — per-file review loop; produces staged findings |
| `secondary-reviewer` | `AiCapabilities` | `MinContextWindow = 128 000`, `SupportedModalities = [Text]` — per-finding two-eyes validation; only active when `TwoEyesConfiguration.Enabled = true` |

---

## Configuration Surface

### `TwoEyesConfiguration`

| Property | Type | Default | Effect |
|---|---|---|---|
| `Enabled` | `bool` | `false` | When `true`, each staged finding is independently reviewed by the `secondary-reviewer` slot before inclusion in the result. When `false`, all staged findings are included with `SecondaryVerdict.NotReviewed`. |

### `WriteBackConfiguration`

| Property | Type | Default | Effect |
|---|---|---|---|
| `MinimumSeverity` | `FindingSeverity` | `Info` | Findings below this severity are not written back as PR comments. Enum order: `Critical > High > Medium > Low > Info`. |
| `PostSummaryToWorkItems` | `bool` | `false` | When `true`, a summary comment is posted to each linked work item via `IWorkItemAccess.PostCommentAsync` after findings are written back. |

### `WorkItemRetrievalFailureBehavior`

| Value | Effect |
|---|---|
| `Ignore` | If `IWorkItemAccess.GetWorkItemsAsync` throws, `ReviewContext.LinkedWorkItems` is set to an empty list and context assembly continues. |
| `Fail` | The exception propagates from `ContextAssembler.AssembleAsync`, aborting the run. |

### Criticality Classification

`CriticalityClassifier` accepts a list of glob patterns. Any changed file whose relative path matches a pattern is classified as `FileCriticality.Critical`. Critical files that receive a `Skipped` verdict from the primary reviewer are automatically reclassified as `FileVerdict.Failed`.

| Configuration point | Meaning |
|---|---|
| Pattern list (empty) | All files are `Normal` — no file can become `Critical`. |
| Pattern `**/*.sql` | Any `.sql` file in the changed set is `Critical`. |
| Pattern `src/core/**` | Any file under `src/core/` is `Critical`. |

---

## Phase Narrative

```
PullRequestReference received
        │
        ▼
Phase 1 — Workflow Registration
  WorkflowBuilder declares five slots and the output descriptor.
        │
        ▼
Phase 2 — Context Assembly (ContextAssembler)
  ISourceControlAccess  → RepositoryWorkingPath
  IPullRequestAccess    → changed-file list, per-file diff hunks, linked work-item refs
  IWorkItemAccess       → batch-fetch linked work items (failure behavior configurable)
  CriticalityClassifier → assigns FileCriticality per file
  Output: immutable ReviewContext
        │
        ▼
Phase 3 — Primary Review Pass (PrimaryReviewOrchestrator)
  Opens a single IAiSession for "primary-reviewer".
  Iterates every ReviewableFile; sends hunk content to the session.
  Records FileVerdict in VerdictMap; stages findings in IStagedFindingsStore.
  Enforces: Skipped verdict on Critical file → FileVerdict.Failed.
  Triggers ContextCompactionService when accumulated token usage exceeds threshold:
    summarises session → disposes → opens fresh session → injects compact summary.
        │
        ▼
Phase 4 — Two-Eyes Pass and Aggregation (TwoEyesPassService + FindingsAggregator)
  When TwoEyesConfiguration.Enabled = false:
    All staged findings are wrapped as ReviewFinding with SecondaryVerdict.NotReviewed.
  When enabled:
    Opens one IAiSession per finding for "secondary-reviewer".
    Records SecondaryVerdict.Approved or SecondaryVerdict.Rejected.
  FindingsAggregator filters to Approved + NotReviewed and builds CodeReviewResult.
        │
        ▼
Phase 5 — Write-Back and Terminal Metrics (WriteBackService + CoverageMetricsProducer + TerminalStatePublisher)
  WriteBackService posts each threshold-passing finding as a PR comment.
  Optionally posts a summary to linked work items.
  CoverageMetricsProducer computes file-level and finding-level coverage metrics.
  TerminalStatePublisher writes code-review-result.json and coverage-metrics.json.
```

---

## Output Artifacts

### `output/code-review-result.json`

Serialised `CodeReviewResult` record:

```json
{
  "prIdentifier": "123",
  "findings": [
    {
      "filePath": "src/Foo.cs",
      "lineStart": 42,
      "lineEnd": 44,
      "severity": "High",
      "category": "Security",
      "message": "Potential SQL injection via string concatenation.",
      "suggestion": "Use parameterised queries.",
      "primaryReviewerAttribution": "primary-reviewer",
      "twoEyesVerdict": "Approved",
      "secondaryReviewerAttribution": "secondary-reviewer"
    }
  ],
  "summary": "Code review complete. 1 finding(s) survived the review pass."
}
```

### `CoverageMetrics` fields

| Field | Type | Description |
|---|---|---|
| `TotalFiles` | `int` | Total files in the changed-file inventory (`ReviewContext.Files.Count`) |
| `ReviewedFileCount` | `int` | Files with `FileVerdict.Reviewed` |
| `SkippedFileCount` | `int` | Files with `FileVerdict.Skipped` |
| `SkippedFiles` | `IReadOnlyList<SkippedFileEntry>` | One entry per skipped file with `FilePath` and `Reason` |
| `FailedFileCount` | `int` | Files with `FileVerdict.Failed` (includes Critical-file skips) |
| `StagedFindingCount` | `int` | Total findings produced by primary reviewer |
| `SurvivingFindingCount` | `int` | Findings in `CodeReviewResult.Findings` after two-eyes filtering |

`ReviewedFileCount + SkippedFileCount + FailedFileCount` always equals `TotalFiles` when all files in `ReviewContext.Files` have been processed.
