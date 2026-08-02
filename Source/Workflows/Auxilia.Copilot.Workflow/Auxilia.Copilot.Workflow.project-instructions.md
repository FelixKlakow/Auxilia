# Auxilia.Copilot.Workflow

Workflow type `github-copilot`: runs an autonomous GitHub Copilot session (the Copilot CLI,
provided by the `github-copilot-cli` slot provider) against the run's workspace. The SECOND
coding agent on the platform — and deliberately a thin shell: the whole session engine
(views, steering, report, repositories) is the shared `AgentSessionApplication` in
`Auxilia.Workflows.AiAgent/CodingAgent`, identical to the Claude Code workflow.

What differs from `Auxilia.ClaudeCode.Workflow`:
- the workflow type name and the declared egress endpoints (`api.githubcopilot.com`,
  `api.github.com`, `github.com`);
- the Docker image bakes Node + `@github/copilot` (and `/usr/local/bin/copilot-stub` for
  system tests — cost rule: never real AI in system tests);
- the agent provider (`Auxilia.Slots.GitHubCopilot`) streams the CLI's plain-text lines,
  not a stream-json transcript.

## Special rules

- Anything Copilot-CLI-specific (arguments, output parsing, token handling) belongs to
  `Auxilia.Slots.GitHubCopilot` — this project must stay CLI-agnostic.
- Never put the token into the dispatch context, chat entries, the report, or logs.
