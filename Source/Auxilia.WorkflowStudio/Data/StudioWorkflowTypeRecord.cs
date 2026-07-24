using Auxilia.UniversalDataAccess;

namespace Auxilia.WorkflowStudio.Data;

/// <summary>
/// A workflow type known to the Studio (the workflow-domain product): its package, declared
/// slots, and declared context keys. Lives in the Studio's own database — the Core knows nothing
/// of workflow types.
/// </summary>
public sealed record StudioWorkflowTypeRecord : IEntity
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string PackageUri { get; init; }

    /// <summary>Declared slots as JSON (<c>DeclaredSlot[]</c>).</summary>
    public string SlotsJson { get; init; } = "[]";

    /// <summary>Declared run-context keys as JSON (<c>string[]</c>).</summary>
    public string ContextKeysJson { get; init; } = "[]";
}
