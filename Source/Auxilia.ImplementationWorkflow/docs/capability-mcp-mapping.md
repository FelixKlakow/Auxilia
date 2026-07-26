# Capability MCP Tool Mapping

Each slot exposes capability operations as MCP tools prefixed with the slot name.
The agent invokes `{slot}.{tool_name}` to perform an action.

For policy configuration, add entries to `SlotConfiguration.Settings["policy:allow"]` (comma-separated operation keys) or `["policy:deny"]`. See BP/21 for full policy configuration details.

---

## `repository` — Source Control (Read + Write)

| MCP Tool | Policy Key | Implementation Pass | Reviewer Pass |
|---|---|---|---|
| `repository.create_branch` | `source_control.create_branch` | ✓ | — |
| `repository.write_file` | `source_control.write_file` | ✓ | — |
| `repository.commit` | `source_control.commit` | ✓ | — |
| `repository.push` | `source_control.push` | ✓¹ | — |
| `repository.list_files` | `source_control.list_files` | ✓ | ✓ |
| `repository.read_file` | `source_control.read_file` | ✓ | ✓ |
| `repository.get_changed_files` | `source_control.get_changed_files` | ✓ | ✓ |

> ¹ **Capability-declared but not production-backed.** `repository.push` / `source_control.push`
> (`ISourceControlWriteAccess.PushAsync`) and `pull-request.open_pull_request`
> (`IPullRequestAccess.OpenPullRequestAsync`) are currently satisfied only by fakes/stubs — there is
> no credentialed production push or PR provider yet. The tools are exposed, but no real remote write occurs.

---

## `task-source` — Task Source

| MCP Tool | Policy Key | Implementation Pass | Reviewer Pass |
|---|---|---|---|
| `task-source.get_work_item` | `task_source.get_work_item` | ✓ | — |
| `task-source.get_work_items` | `task_source.get_work_items` | ✓ | — |
| `task-source.update_status` | `task_source.update_status` | ✓ | — |
| `task-source.post_comment` | `task_source.post_comment` | ✓ | — |

---

## `test-runner` — Test Runner

| MCP Tool | Policy Key | Implementation Pass | Reviewer Pass |
|---|---|---|---|
| `test-runner.run_tests` | `test_runner.run_tests` | ✓ | — |

---

## `pull-request` — Pull Request Access

| MCP Tool | Policy Key | Implementation Pass | Reviewer Pass |
|---|---|---|---|
| `pull-request.get_changed_files` | `pull_request.get_changed_files` | — | ✓ |
| `pull-request.get_diff_hunks` | `pull_request.get_diff_hunks` | — | ✓ |
| `pull-request.get_comments` | `pull_request.get_comments` | — | ✓ |
| `pull-request.get_linked_work_items` | `pull_request.get_linked_work_items` | — | ✓ |
| `pull-request.post_comment` | `pull_request.post_comment` | — | ✓ |
| `pull-request.open_pull_request` | `pull_request.open_pull_request` | ✓¹ | — |

---

## `implementation-agent` — AI Agent (Implementer)

| MCP Tool | Policy Key | Implementation Pass | Reviewer Pass |
|---|---|---|---|
| `implementation-agent.run_inference` | `ai_agent.run_inference` | ✓ | — |

---

## `reviewer-agent` — AI Agent (Reviewer)

| MCP Tool | Policy Key | Implementation Pass | Reviewer Pass |
|---|---|---|---|
| `reviewer-agent.run_inference` | `ai_agent.run_inference` | — | ✓ |

---

## Policy Configuration Example

```json
{
  "slotName": "repository",
  "settings": {
    "policy:deny": "source_control.commit,source_control.push"
  }
}
```

See BP/21 for the complete policy configuration reference and the `PolicyGuardedSourceControlWriteAccess` decorator specification.
