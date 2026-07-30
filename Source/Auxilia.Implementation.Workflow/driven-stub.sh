#!/bin/sh
# DRIVEN stand-in for the interactive author CLI in system tests (cost rule: never real AI
# in system tests). The implementation workflow drives prompts into this stub via
# `tmux send-keys` (one prompt = one stdin line) and awaits turn completion through the
# Claude hook contract: after EVERY processed line the stub POSTs a Stop payload to the
# loopback hook listener whose port it reads from the settings ClaudeInteractiveLogin wrote
# (~/.claude/settings.json). It produces the same .auxilia exchange files a real author
# would (paths relative to the cwd the session was started in — the workspace) and never
# exits on its own: the workflow ends the tmux session. Providers without a hook system
# (Copilot) announce the listener port in ~/.auxilia/console-hook-port instead — the same
# wire, found via the fallback below.

HOOK_PORT=$(sed -n 's/.*127\.0\.0\.1:\([0-9][0-9]*\).*/\1/p' "$HOME/.claude/settings.json" 2>/dev/null | head -n 1)
[ -n "$HOOK_PORT" ] || HOOK_PORT=$(cat "$HOME/.auxilia/console-hook-port" 2>/dev/null)

signal_stop() {
  [ -n "$HOOK_PORT" ] || return 0
  curl -s -m 3 -X POST --data '{"hook_event_name":"Stop"}' \
    "http://127.0.0.1:$HOOK_PORT/" >/dev/null 2>&1 || true
}

echo "driven-stub ready (hook port: ${HOOK_PORT:-none})"

while read -r LINE; do
  mkdir -p .auxilia

  # Completeness drive: the story is always complete enough — no open questions.
  case "$LINE" in
    *COMPLETE*)
      printf 'NONE\n' > .auxilia/questions.md
      ;;
  esac

  # Plan drive (case-insensitive "plan"): a small but real markdown plan.
  case "$LINE" in
    *[Pp][Ll][Aa][Nn]*)
      {
        echo '# Implementation plan'
        echo
        echo '- Append the implemented marker to the workspace'
        echo '- Run no tests (stub)'
      } > .auxilia/plan.md
      ;;
  esac

  # Implementation drive: a REAL workspace change so git status/diff are non-empty
  # (README.md is tracked in the test repository — appending to it yields a real diff).
  case "$LINE" in
    *Implement*)
      printf 'implemented by driven-stub\n' >> implemented.txt
      if [ -f README.md ]; then
        printf '\nimplemented by driven-stub\n' >> README.md
      fi
      ;;
  esac

  # Simulation pacing: DRIVEN_STUB_DELAY (seconds per turn) makes the live view watchable.
  sleep "${DRIVEN_STUB_DELAY:-0.2}"
  signal_stop
done

# stdin closed (does not happen under tmux) — stay alive until the workflow kills the session.
while :; do sleep 60; done
