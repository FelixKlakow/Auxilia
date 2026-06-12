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

    public static Guid IdFor(string name) => DeterministicGuid.For("scheduled-trigger", name);
}
