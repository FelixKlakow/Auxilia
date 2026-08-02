# Auxilia.Slots.GitHub.Tests

Unit tests for `Auxilia.Slots.GitHub` — the GitHub repository slot (handler, `GitHubRepositoryOptions`, and the REST-backed `GitHubRepositoryAccess`).

## Special Rules
`GitHubRepositoryAccess` is a GitHub REST client driven through a `StubHttpMessageHandler` (canned response body/status, captures `LastRequest`) — no network. Tests assert exact request URIs, Accept/Bearer/User-Agent headers, and that a failed request never leaks the token.

## File / Folder Map
```
Tests/Slots/Auxilia.Slots.GitHub.Tests/
└── UnitTests/
    ├── GitHubRepositoryOptionsTests.cs      # FromSettings: owner/repo parse from RepositoryUrl, .git/trailing-slash normalization, Branch/Token defaults, unparseable / missing-url throws
    ├── GitHubRepositorySlotHandlerTests.cs  # DI registration → ISourceControlAccess/GitHubRepositoryAccess; missing-url / unknown-slot throws
    └── GitHubRepositoryAccessTests.cs        # ListFiles tree mapping + path-prefix filter; ReadFileContent raw body; GetChangedFiles status → ChangeKind; auth/User-Agent headers; token-not-leaked on error; empty WorkingPath
```
