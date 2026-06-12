namespace Auxilia.BackendService.PlatformHost;

/// <summary>Configuration for the Backend Service's platform-host responsibilities.</summary>
public sealed class PlatformHostSettings
{
    /// <summary>A Steering Instance whose heartbeat is older than this is considered dead.</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 30;

    /// <summary>How often the heartbeat monitor scans for dead instances.</summary>
    public int MonitorIntervalSeconds { get; set; } = 10;

    /// <summary>How often the scheduler checks for due triggers.</summary>
    public int SchedulerIntervalSeconds { get; set; } = 10;

    /// <summary>Dispatch command queue consumed by the Steering Instance pool.</summary>
    public string CommandQueueName { get; set; } = "workflow.run-commands";

    /// <summary>Re-dispatch runs orphaned by a dead Steering Instance (once per run).</summary>
    public bool RedispatchOnFailover { get; set; } = true;
}
