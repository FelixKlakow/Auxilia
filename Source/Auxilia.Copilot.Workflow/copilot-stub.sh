#!/bin/sh
# Stand-in for the GitHub Copilot CLI in system tests (cost rule: never real AI in system
# tests). Speaks just enough of `copilot -p ... --allow-all-tools` — plain text progress on
# stdout, exit 0 on success — to exercise the provider's line streaming and the report
# pipeline, and leaves a note file in the workspace like a real agent edit would.
sleep 0.2
echo "I'll take a look at the workspace before I start."
sleep 0.2
echo "Running: ls"
sleep 0.2
printf 'Copilot stub was here.\n' > COPILOT_NOTES.md
echo "Created COPILOT_NOTES.md with my findings."
sleep 0.2
echo "Done - the task is complete: findings written to COPILOT_NOTES.md."
exit 0
