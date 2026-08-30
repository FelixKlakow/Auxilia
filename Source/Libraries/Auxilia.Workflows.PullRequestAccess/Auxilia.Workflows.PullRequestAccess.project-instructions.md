# Auxilia.Workflows.PullRequestAccess

Workflow capability library for pull-request host access: declares the `IPullRequestAccess`
capability a workflow requires and ships everything the runtime binds to it — the capability
record, the builder extension, the policy-guard decorator, and the MCP tool server. The concrete
provider (GitHub/ADO/…) is supplied by a separate slot package.

## Special Rules

- `PolicyGuardedPullRequestAccess` gates every `*Async` call on
  `IToolPolicy.IsAllowed(PullRequestOperation.*)` and throws `ToolPolicyDeniedException` when
  denied — it is the enforcement point; the raw provider is never handed to a workflow.
- `PullRequestAccessCapabilities.Extensions` (`[JsonExtensionData]`) is an opaque forward-compat
  passthrough — never read it for capability/permission logic.
- MCP tool names are slot-prefixed via `SlotMcpPrefix.Format`; each tool catches and stringifies
  its own errors rather than throwing across the MCP boundary.

## File / Folder Map
```
Source/Libraries/Auxilia.Workflows.PullRequestAccess/
├── IPullRequestAccess.cs                          # runtime contract (changed files, diffs, comments, open PR)
├── PullRequestAccessCapabilities.cs               # ICapability: RequiredPermissions[], PrHostType? (open string vocabulary — SourceHostTypes constants, OrdinalIgnoreCase)
├── PullRequestPermission.cs                       # Read / Write
├── PullRequestOperation.cs                        # policy-keyed operation enum
├── PullRequestAccessWorkflowBuilderExtensions.cs  # RequiresPullRequestAccess() over builder.Requires<T>
├── PolicyGuardedPullRequestAccess.cs              # IToolPolicy decorator around IPullRequestAccess
├── PullRequestOptions.cs                          # open-PR arguments (title, branches, linked items)
├── ChangedFile.cs / ChangeKind.cs                 # changed-file entry + Added/Modified/Deleted/Renamed
├── DiffHunk.cs                                    # unified-diff hunk record
├── ReviewComment.cs                               # review-comment record
├── WorkItemReference.cs                           # linked work-item record
└── Mcp/PullRequestAccessMcpTools.cs               # MCP tool server exposing the operations to AI agents
```
