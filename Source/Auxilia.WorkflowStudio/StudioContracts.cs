namespace Auxilia.WorkflowStudio;

/// <summary>A capability slot a workflow type declares (the workflow-domain shape).</summary>
public sealed record DeclaredSlot(string Name, string Contract, bool Optional = false);

/// <summary>Register (or update) a workflow type in the Studio catalog.</summary>
public sealed record RegisterWorkflowType(
    string Name,
    string DisplayName,
    string PackageUri,
    IReadOnlyList<DeclaredSlot> Slots,
    IReadOnlyList<string> ContextKeys);

/// <summary>A workflow type as seen by clients of the Studio.</summary>
public sealed record WorkflowTypeDto(
    Guid Id,
    string Name,
    string DisplayName,
    string PackageUri,
    IReadOnlyList<DeclaredSlot> Slots,
    IReadOnlyList<string> ContextKeys);

/// <summary>Binds a declared slot to a Core connector instance (by id — secrets stay in the Core).</summary>
public sealed record StudioSlotBinding(string SlotName, Guid ConnectorId);

/// <summary>Author a runnable configuration of a workflow type by binding its slots to Core connectors.</summary>
public sealed record ConfigureWorkflow(
    string Name,
    string WorkflowTypeName,
    IReadOnlyList<StudioSlotBinding> SlotBindings,
    IReadOnlyDictionary<string, string>? Context = null);

/// <summary>The Core run configuration the Studio produced from a domain configuration.</summary>
public sealed record ConfiguredWorkflowDto(Guid CoreConfigurationId, string Name, string WorkflowTypeName);
