# Auxilia.Workflows.SourceControl

Slot-package that adds source-control vocabulary to the workflow SDK. Declares what SCM permissions and host types a workflow needs. The runtime `ISourceControl` interface is injected after slot resolution by a separate provider package.

## File / Folder Map
```
Source/Auxilia.Workflows.SourceControl/
├── ISourceControl.cs                           # Runtime SCM interface injected into workflows
├── SourceControlCapabilities.cs                # ICapability: RequiredPermissions[], SupportedHostTypes?
├── Permission.cs                               # Enum of SCM permissions (Read, Write, Admin…)
└── SourceControlWorkflowBuilderExtensions.cs   # RequiresSourceControl() — thin wrapper over builder.Requires<T>
```