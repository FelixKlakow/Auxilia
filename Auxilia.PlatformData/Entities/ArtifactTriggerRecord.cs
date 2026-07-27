using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>
/// Artifact-completion trigger (ARCHITECTURE §6 trigger model): when an artifact of
/// <see cref="ArtifactType"/> is persisted, the configured follow-up workflow is dispatched —
/// workflow chaining without coupling the workflows to each other.
/// </summary>
public sealed record ArtifactTriggerRecord : IEntity
{
    public Guid Id { get; init; }
    public required string ArtifactType { get; init; }
    public required string WorkflowType { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>Principal on whose behalf the chained dispatch runs (policy-checked).</summary>
    public Guid? RunAsPrincipalId { get; init; }
    /// <summary>When set, dispatches reference this named workflow configuration (#20).</summary>
    public Guid? WorkflowConfigurationId { get; init; }

    public static Guid IdFor(string artifactType, string workflowType)
        => DeterministicGuid.For("artifact-trigger", artifactType, "", workflowType);

    /// <summary>Deterministic ID for the single chaining rule wired by a workflow configuration's editor.</summary>
    public static Guid IdForConfiguration(string configurationName)
        => DeterministicGuid.For("artifact-trigger", "workflow-configuration", "", configurationName);
}
