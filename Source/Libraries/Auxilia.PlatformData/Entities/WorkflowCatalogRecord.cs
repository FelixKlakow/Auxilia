using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Admin curation of one registered workflow type: whether it may be configured and run.
/// Kept separate from <see cref="WorkflowPackageRecord"/> so re-registration never wipes
/// curation. No record means enabled — disabling is the administrative act.
/// </summary>
public sealed record WorkflowCatalogRecord : IEntity
{
    public Guid Id { get; init; }
    public required string WorkflowType { get; init; }
    public bool Enabled { get; init; } = true;

    public static Guid IdFor(string workflowType) => DeterministicGuid.For("workflow-catalog", workflowType);
}
