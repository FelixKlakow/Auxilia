# Auxilia.Workflows.TaskSource

Slot-package that adds task/issue-tracker vocabulary to the workflow SDK. Declares what work-item types a workflow can consume. The runtime access contracts injected after slot resolution by a separate provider package are `IWorkItemAccess` (read + comment + attachments) and `ITaskSourceAccess` (read + comment + status update). `ITaskSource` is an obsolete empty marker only — it is not the injected interface.

## File / Folder Map
```
Source/Libraries/Auxilia.Workflows.TaskSource/
├── ITaskSource.cs                           # Obsolete empty marker ([Obsolete] → use IWorkItemAccess)
├── IWorkItemAccess.cs                       # Injected contract: GetWorkItem(s), PostComment, GetAttachments
├── ITaskSourceAccess.cs                     # Injected contract: GetWorkItem(s), PostComment, UpdateStatus
├── WorkItem.cs                              # Work-item record (+ WorkItemAttachment lives in IWorkItemAccess.cs)
├── PolicyGuardedTaskSourceAccess.cs         # Allow/deny policy decorator over task-source access
├── Mcp/WorkItemAccessMcpTools.cs            # MCP tools exposing work-item reads to the agent
├── Mcp/TaskSourceMcpTools.cs                # MCP tools exposing task-source operations to the agent
├── TaskSourceOperation.cs                   # Operation keys used by the policy decorator
├── TaskSourceCapabilities.cs                # ICapability: SupportedItemTypes[]
├── ItemType.cs                              # Enum of work-item types (Bug, Feature, Task, Epic…)
└── TaskSourceWorkflowBuilderExtensions.cs   # RequiresTaskSource() — thin wrapper over builder.Requires<T>
```