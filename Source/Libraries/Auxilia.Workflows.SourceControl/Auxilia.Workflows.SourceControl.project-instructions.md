# Auxilia.Workflows.SourceControl

Slot-package that adds source-control vocabulary to the workflow SDK. Declares what SCM permissions and host types a workflow needs. The runtime access contract injected after slot resolution by a separate provider package is `ISourceControlAccess` (read) plus `ISourceControlWriteAccess` (branch/commit/write/push). `ISourceControl` is an obsolete empty marker only — it is not the injected interface.

> Write access (`ISourceControlWriteAccess`, incl. `PushAsync`) is **capability-declared but not production-backed** — it is currently satisfied only by fakes/stubs; there is no credentialed production push provider yet.

## File / Folder Map
```
Source/Libraries/Auxilia.Workflows.SourceControl/
├── ISourceControl.cs                           # Obsolete empty marker ([Obsolete] → use ISourceControlAccess)
├── ISourceControlAccess.cs                     # Injected read contract: WorkingPath, ListFiles, ReadFileContent, GetChangedFiles
├── ISourceControlWriteAccess.cs                # Injected write contract: CreateBranch/WriteFile/Commit/Push (fake-backed)
├── PolicyGuardedSourceControlAccess.cs         # Allow/deny policy decorator over ISourceControlAccess
├── PolicyGuardedSourceControlWriteAccess.cs    # Allow/deny policy decorator over ISourceControlWriteAccess
├── Mcp/SourceControlAccessMcpTools.cs          # MCP tools exposing read operations to the agent
├── Mcp/SourceControlWriteAccessMcpTools.cs     # MCP tools exposing write/push operations to the agent
├── ChangedFile.cs / ChangeKind.cs              # Changed-file record + change-kind enum
├── SourceControlOperation.cs                   # Operation keys used by the policy decorators
├── SourceControlCapabilities.cs                # ICapability: RequiredPermissions[], SupportedHostTypes?
├── Permission.cs                               # Enum of SCM permissions (Read, Write, Admin…)
└── SourceControlWorkflowBuilderExtensions.cs   # RequiresSourceControl() — thin wrapper over builder.Requires<T>
```