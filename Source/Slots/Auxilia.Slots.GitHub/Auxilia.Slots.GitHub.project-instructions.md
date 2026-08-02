# Auxilia.Slots.GitHub

Slot provider `github-repository` for a workflow's `repository` slot: exposes a GitHub
repository as the workspace (`ISourceControlAccess`) over the GitHub REST API v3 — list
files via the git trees endpoint, read raw file contents, and diff refs via compare.

Builds `Auxilia.Slots.GitHub.slothandler.dll` (see `<AssemblyName>`) with the
`*.slothandler.manifest.json` sidecar — the naming contract of `FileSystemPluginDiscovery`.
Manifest settings: `RepositoryUrl` (Text, required), `Token` (Secret — fine-grained PAT,
only for private repos), `Branch` (Text, defaults to `main`).

## Structure

- `GitHubRepositorySlotHandler` — `ISlotHandler`; parses settings into options, registers
  `ISourceControlAccess`. Fails registration when `RepositoryUrl` is missing or unparseable.
- `GitHubRepositoryOptions` — typed settings view; derives owner/repo from the URL.
- `GitHubRepositoryAccess` — the API client; the optional `HttpMessageHandler` constructor
  parameter is the only HTTP seam, injected by unit tests.

## Special rules

- The token is sent ONLY as the `Authorization: Bearer` header — never in error messages,
  logs, or URLs. `WorkingPath` is empty: there is no mounted checkout.
- Every request carries a `User-Agent: Auxilia` header; GitHub rejects requests without one.
