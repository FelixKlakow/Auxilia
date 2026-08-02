# Auxilia.Implementation.Workflow

Workflow type `implementation` (long-living): the full assisted-delivery pipeline — a user
story (TFS/Azure DevOps or mail) is refined, planned, implemented, reviewed
(AI + operator gates), pushed by the WORKFLOW, and its state set from the source's own
vocabulary. **Design (read first): `docs/implementation-workflow-design.md`.** Replaces the
retired old-generation `Auxilia.ImplementationWorkflow`.

## How it hangs together

- **`ImplementationPipeline`** orchestrates over an **`AgentConsolePool`** of driven consoles
  (tmux+ttyd; the AUTHOR serves the run terminal and spans refinement/plan/implementation by
  default; `plan-agent`/`implement-agent`/`review-by` select fresh or reviewer-bound consoles
  per stage) — prompts go in via `tmux send-keys` (ONE line per drive), turn completion comes
  from the provider's console events (Claude: hooks) fanned out by `ConsoleSessionEventHub`.
- Stage drives are `<instructions input, defaulted from ImplementationPrompts>` + the fixed
  file contract; the plan stage's default demands a mermaid `## Design` section.
- The workflow↔agent exchange is FILES under `.auxilia/` in the workspace (story.md, plan.md,
  questions.md, verdict files) — never parsed chat output.
- **`IReviewRunner`**: `HeadlessReviewRunner` (second `ICodingAgent`, keyed slot
  `review-agent`) or `ConsoleReviewRunner` (one-shot CLI in a second tmux session,
  completion = session exit). Verdict files start with APPROVE/REVISE.
- Operator gates ride `OperatorChannel.AskAsync` forms; `GateAsync` applies the
  **idle-compaction rule** (author `/compact` after `gate-idle-compaction` minutes).
- Push: the WORKFLOW commits/pushes (`IGitRunner`) through the AllowPush repository binding —
  the agent is told to commit nothing.
- Story state: `IWorkItemAccess.GetStatesAsync` feeds the closing choice gate;
  `SetStateAsync` + a comment close the loop.

## Special rules

- The agent NEVER performs git actions; drives always end with "commit nothing".
- All structured agent output is file-based (`.auxilia/`), never "respond with JSON".
- The Docker image bakes BOTH CLIs (claude + copilot) + tmux/ttyd + both stubs; system tests
  select stubs via the providers' CliPath settings.
