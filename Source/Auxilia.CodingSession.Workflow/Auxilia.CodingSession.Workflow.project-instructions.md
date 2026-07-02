# Auxilia.CodingSession.Workflow

Live interactive coding session as a LongLiving workflow: the real Claude Code CLI runs
inside this container against the Workspace-Manager-mounted repository, viewable and
steerable through the platform's authenticated web terminal, and the run completes when
the CLI exits.

## How the session works

- `TmuxSessionHost` starts the CLI in a **detached tmux session** whose command line ends
  with `; tmux kill-server` — the CLI's exit tears the tmux server down, which is the
  auto-exit contract. `ttyd` (container port 7681) serves `tmux attach`, so any number of
  browser viewers can attach and detach without ever killing the CLI.
- `CodingSessionApplication` orchestrates: local session branch (`cc-session/<run-tag>`)
  → host up → wait for tmux death (bounded by `CODING_SESSION_MAX_MINUTES`, default 240)
  → collect changed-file NAMES (committed diff + porcelain status, deduplicated) → write
  the `coding-session-result` artifact.

## Invariants

- **No credentials in this container beyond the CLI's own token.** Git operations are
  LOCAL ONLY (branch, diff); pushing is the Workspace Manager's job. The personal-account
  OAuth token arrives as `CLAUDE_CODE_OAUTH_TOKEN` via JIT slot activation and must never
  be logged, written to disk, or passed as a CLI argument.
- **System tests never run real AI**: `CODING_SESSION_CLI=claude-session-stub` selects
  the baked stub script; the real `claude` binary is the dev-stand/manual path.
- Changed-file reporting carries file NAMES only — contents never leave the workspace
  through the artifact.
- The Windows-container variant is a second registered package with its own image; do not
  branch on OS inside this code.
