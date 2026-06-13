using Auxilia.UniversalDataAccess;

namespace Auxilia.PlatformData.Entities;

/// <summary>A recurring time-based workflow trigger executed by the platform scheduler.</summary>
public sealed record ScheduledTriggerRecord : IEntity
{
    public Guid Id { get; init; }
    public required string WorkflowType { get; init; }
    public required string WorkflowPackageUri { get; init; }
    public int IntervalSeconds { get; init; }
    public bool Enabled { get; init; } = true;
    public string? ContextJson { get; init; }
    /// <summary>Principal on whose behalf the scheduled dispatches run (policy-checked).</summary>
    public Guid? RunAsPrincipalId { get; init; }
    public DateTimeOffset? LastDispatchedUtc { get; init; }
    /// <summary>When set, dispatches reference this named workflow configuration (#20).</summary>
    public Guid? WorkflowConfigurationId { get; init; }

    public static Guid IdFor(string name) => DeterministicGuid.For("scheduled-trigger", name);

    /// <summary>Deterministic ID for the single schedule wired by a workflow configuration's editor.</summary>
    public static Guid IdForConfiguration(string configurationName)
        => DeterministicGuid.For("scheduled-trigger", "workflow-configuration", "", configurationName);
}
