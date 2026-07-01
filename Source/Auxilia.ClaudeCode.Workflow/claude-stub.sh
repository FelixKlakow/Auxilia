#!/bin/sh
# Stand-in for the Claude Code CLI in system tests (cost rule: never real AI in system
# tests). Speaks just enough of `claude -p ... --output-format stream-json --verbose` to
# exercise the real parser, live-view, and report pipeline, and leaves a note file in the
# workspace like a real agent edit would. The small sleeps make the live view visibly stream.
sleep 0.2
echo '{"type":"system","subtype":"init","session_id":"stub-session","model":"claude-stub","tools":["Bash","Read","Write"]}'
sleep 0.2
echo '{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Let me take a look at the workspace before I start."}]}}'
sleep 0.2
echo '{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_stub_1","name":"Bash","input":{"command":"ls"}}]}}'
sleep 0.2
echo '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_stub_1","content":"repos","is_error":false}]}}'
sleep 0.2
printf 'Stub agent was here.\n' > STUB_NOTES.md
echo '{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_stub_2","name":"Write","input":{"file_path":"STUB_NOTES.md"}}]}}'
sleep 0.2
echo '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_stub_2","content":"File created successfully","is_error":false}]}}'
sleep 0.2
echo '{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done - I noted my findings in STUB_NOTES.md."}]}}'
sleep 0.2
echo '{"type":"result","subtype":"success","is_error":false,"duration_ms":1800,"num_turns":3,"result":"Stub session completed: findings written to STUB_NOTES.md.","total_cost_usd":0.0042}'
exit 0
