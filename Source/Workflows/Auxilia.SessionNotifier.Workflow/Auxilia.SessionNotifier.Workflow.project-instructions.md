# Auxilia.SessionNotifier.Workflow

Chained consumer of `coding-session-result` artifacts: posts the finished session's
branch and changed-file NAMES as a comment on the originating work item. With the email
work-items provider bound, that comment is the reply mail to whoever started the session.

## Invariants

- Consumes the artifact from the file the dispatcher materialized
  (`WORKFLOW_CONSUMED_ARTIFACT` under `/workflow-output/consumed/`) — artifact payloads
  never travel over the message bus.
- The summary carries file NAMES only, never file contents or diffs — nothing sensitive
  may leave through a mailbox.
- `ConsumesArtifact("coding-session-result")` is the declared chaining criteria: the flow
  view offers this workflow exactly on coding-session outputs.
