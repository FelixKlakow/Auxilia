# Auxilia.Workflows.TaskSource

Slot-package that adds task/issue-tracker vocabulary to the workflow SDK. Declares what work-item types a workflow can consume. The runtime `ITaskSource` interface is injected after slot resolution by a separate provider package.

## File / Folder Map
```
Source/Auxilia.Workflows.TaskSource/
├── ITaskSource.cs                           # Runtime interface — GetWorkItemAsync, GetWorkItemsAsync
├── WorkItem.cs                              # record(Id, Title, Description?, Type) — returned by ITaskSource
├── TaskSourceCapabilities.cs                # ICapability: SupportedItemTypes[]
├── ItemType.cs                              # Enum of work-item types (Bug, Feature, Task, Epic…)
└── TaskSourceWorkflowBuilderExtensions.cs   # RequiresTaskSource() — thin wrapper over builder.Requires<T>
```