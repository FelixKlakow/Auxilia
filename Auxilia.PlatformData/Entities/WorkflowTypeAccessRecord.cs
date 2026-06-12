using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Per-workflow-type access list entry: grants an action on a workflow type to a role or
/// an individual principal. When any entry exists for (workflow type, action), only matching
/// principals are allowed; without entries the role permission set decides.
/// </summary>
public sealed record WorkflowTypeAccessRecord : IEntity
{
    public Guid Id { get; init; }
    public required string WorkflowType { get; init; }
    public required string Action { get; init; }
    public string? RoleName { get; init; }
    public Guid? PrincipalId { get; init; }

    public static Guid IdFor(string workflowType, string action, string subject)
        => DeterministicGuid.For("workflow-type-access", workflowType, "", action, "", subject);
}
