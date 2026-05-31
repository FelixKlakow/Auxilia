# Auxilia.ImplementationWorkflow

Autonomous AI implementation workflow. Receives a work-item reference, checks out a branch, runs an AI
implementation agent equipped with source-control, test-runner, and task-source tools, optionally runs a
read-only reviewer pass via `ReviewerOrchestrator`, opens a pull request, and emits output signals.

## Architecture

```mermaid
flowchart LR
    A[Context Assembly] --> B[Branch Setup]
    B --> C[AgentOrchestrator]
    C --> D[ReviewerOrchestrator]
    D --> E[Pull Request]
    E --> F[Write-back / Signals]
```

**AgentOrchestrator** drives the implementation pass. It does **not** use a result-sink because the
agent's output is the committed code, not structured data.

**ReviewerOrchestrator** runs a read-only review pass when
`ImplementationWorkflowConfiguration.ReviewerEnabled = true`. It registers three MCP tool servers in
`AiSessionOptions.CapabilityTools`:

- `SourceControlAccessMcpTools` — read-only SCM access for the reviewer
- `PullRequestAccessMcpTools` — read-only PR access for the reviewer
- `ImplementationReviewResultSinkMcpTools` (from `Mcp/`) — result-sink for structured review notes

After `ExecuteAsync`, `DrainNotes()` returns the collected `ReviewNote` records.
`ReviewNoteSeverity` is a typed enum (`Info`, `Warning`, `Error`) — never a raw string.
All three tool servers are stopped in the `finally` block.

## File / Folder Map

```
Source/Auxilia.ImplementationWorkflow/
├── ImplementationWorkflow.cs                   # Workflow entry point
├── AgentOrchestrator.cs                        # Drives the implementation agent pass
├── ReviewerOrchestrator.cs                     # Optional read-only reviewer pass
├── Context/                                    # ImplementationContext assembly
├── Branch/                                     # Branch creation and naming helpers
├── Signals/                                    # Completed, ReviewNotesFlagged, Failed signal records
├── Mcp/                                        # ImplementationReviewResultSinkMcpTools — result-sink for review notes
├── ReviewNote.cs                               # ReviewNote(Description, FilePath?, Severity: ReviewNoteSeverity)
├── ReviewNoteSeverity.cs                       # Enum: Info, Warning, Error
├── WriteBackService.cs                         # Posts comments and updates work-item status
└── ServiceCollectionExtensions.cs              # AddImplementationWorkflow() DI registration
```

## Special rules

- `ImplementationReviewResultSinkMcpTools` (`Mcp/`) is the **only mechanism** for `ReviewerOrchestrator`
  to collect `ReviewNote` records. Never parse `IAiSession.ExecuteAsync` responses.
- `ReviewNote.Severity` is typed as `ReviewNoteSeverity` — never a raw string after blueprint/36.
- The result-sink server is started before `IAiAgent.OpenSessionAsync` alongside `scmTools` and
  `prTools`, and stopped in the `finally` block with the other tools.
- Do not keep `ParseReviewNotes`, `ReviewNoteDto`, or `catch (JsonException)` fallbacks — these are
  regressions.
