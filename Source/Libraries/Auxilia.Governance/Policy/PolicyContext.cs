namespace Auxilia.Governance.Policy;

/// <summary>One authorization request: (principal, action, resource).</summary>
public sealed record PolicyContext(
    Guid PrincipalId,
    string Action,
    /// <summary>The concrete resource the action applies to, e.g. a run or workflow instance ID.</summary>
    string Resource)
{
    /// <summary>
    /// Workflow type the resource belongs to. When set, workflow-type access list entries
    /// for (type, action) take precedence over role permissions.
    /// </summary>
    public string? WorkflowType { get; init; }
}
