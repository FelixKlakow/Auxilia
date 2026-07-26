namespace Auxilia.WorkflowStudio.Triggers;

/// <summary>Configuration for the Studio's trigger schedulers (workflow-domain triggers).</summary>
public sealed class TriggerSettings
{
    /// <summary>How often the scheduler checks for due interval triggers.</summary>
    public int SchedulerIntervalSeconds { get; set; } = 10;
}
