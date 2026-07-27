#!/bin/sh
# Stand-in for the Claude Code CLI in system tests (cost rule: never real AI in system
# tests). Speaks just enough of `claude -p ... --output-format stream-json --verbose` to
# exercise the real parser, live-view, and report pipeline, and leaves a note file in the
# workspace like a real agent edit would. The small sleeps make the live view visibly stream.
#
# With `--input-format stream-json` it speaks the INTERACTIVE protocol like the real CLI:
# the instruction arrives as the first stream-json user message on stdin, a `can_use_tool`
# control request is raised (and must be answered with a control_response before the session
# continues), and — when the instruction contains AWAIT_GUIDANCE — one more user message is
# awaited and echoed, exercising the operator-guidance path.

INTERACTIVE=0
for arg in "$@"; do
  [ "$arg" = "--input-format" ] && INTERACTIVE=1
done

INSTRUCTION=""
if [ "$INTERACTIVE" = "1" ]; then
  # First stdin line = the initial user message carrying the instruction.
  read -r FIRST_LINE
  INSTRUCTION=$(printf '%s' "$FIRST_LINE" | sed 's/.*"text":"\([^"]*\)".*/\1/')
fi

sleep 0.2
echo '{"type":"system","subtype":"init","session_id":"stub-session","model":"claude-stub","tools":["Bash","Read","Write"]}'
sleep 0.2
echo '{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Let me take a look at the workspace before I start."}]}}'
sleep 0.2
echo '{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_stub_1","name":"Bash","input":{"command":"ls"}}]}}'
sleep 0.2
echo '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_stub_1","content":"repos","is_error":false}]}}'
sleep 0.2

ALLOWED=1
if [ "$INTERACTIVE" = "1" ]; then
  # Permission prompt: block until the operator's control_response arrives on stdin.
  echo '{"type":"control_request","request_id":"ctrl_stub_1","request":{"subtype":"can_use_tool","tool_name":"Write","input":{"file_path":"STUB_NOTES.md"},"permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Write","ruleContent":"STUB_NOTES.md"}],"behavior":"allow","destination":"session"}]}}'
  while read -r LINE; do
    case "$LINE" in
      *control_response*) break ;;
    esac
  done
  case "$LINE" in
    *'"behavior":"allow"'*) ALLOWED=1 ;;
    *) ALLOWED=0 ;;
  esac
fi

if [ "$ALLOWED" = "1" ]; then
  printf 'Stub agent was here.\n' > STUB_NOTES.md
  echo '{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_stub_2","name":"Write","input":{"file_path":"STUB_NOTES.md"}}]}}'
  sleep 0.2
  echo '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_stub_2","content":"File created successfully","is_error":false}]}}'
else
  echo '{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Understood - I will not write the note file."}]}}'
fi
sleep 0.2

case "$INSTRUCTION" in
  *AWAIT_GUIDANCE*)
    # Guidance path: block for one more user message and echo what the operator said.
    while read -r LINE; do
      case "$LINE" in
        *'"type":"user"'*) break ;;
      esac
    done
    GUIDANCE=$(printf '%s' "$LINE" | sed 's/.*"text":"\([^"]*\)".*/\1/')
    printf '{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Applying your guidance: %s"}]}}\n' "$GUIDANCE"
    sleep 0.2
    ;;
esac

echo '{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done - I noted my findings in STUB_NOTES.md."}]}}'
sleep 0.2
echo '{"type":"result","subtype":"success","is_error":false,"duration_ms":1800,"num_turns":3,"result":"Stub session completed: findings written to STUB_NOTES.md.","total_cost_usd":0.0042}'
exit 0
