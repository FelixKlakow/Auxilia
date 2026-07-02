#!/usr/bin/env bash
# Stand-in for the interactive Claude Code CLI in system tests (cost rule: never real AI).
# Simulates a short session: writes a file into the workspace, waits briefly so the
# LongLiving run is observably running, then exits — which ends tmux and the run.
set -euo pipefail
echo "stub coding session started in $(pwd)"
echo "stub change from coding session" > STUB_SESSION_NOTES.md
sleep "${STUB_SESSION_SECONDS:-3}"
echo "stub coding session done"
