#!/usr/bin/env bash
# Stand-in for the interactive Claude Code CLI in system tests and the screenshot harness
# (cost rule: never real AI). Prints a session-like banner, makes a small change, then stays
# alive for STUB_SESSION_SECONDS so the live web terminal is observable before it exits
# (which ends tmux and completes the run).
set -euo pipefail
echo "┌───────────────────────────────────────────────┐"
echo "│  Claude Code (stub)  —  Auxilia live session   │"
echo "└───────────────────────────────────────────────┘"
echo "workspace: $(pwd)"
echo
echo "> Looking around the repository ..."
ls -la 2>/dev/null | head -n 15 || true
echo
echo "> I noted my findings in SESSION_NOTES.md."
printf 'Reviewed the workspace during an Auxilia live coding session.\n' > SESSION_NOTES.md
echo
echo "This is a live terminal — the real CLI would be interactive here."
echo "Type /exit when finished; the session then closes and the run completes."
sleep "${STUB_SESSION_SECONDS:-3}"
echo "session done"
