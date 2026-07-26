namespace Auxilia.Core.Api;

/// <summary>Host configuration for the Core API (bound from the <c>CoreApi</c> section).</summary>
public sealed class CoreApiSettings
{
    /// <summary>Queue the runner pool consumes run commands from (competing consumers).</summary>
    public string RunCommandQueue { get; set; } = "workflow.run-commands";

    /// <summary>Queue the runner consumes cancellation commands from.</summary>
    public string CancelCommandQueue { get; set; } = "workflow.cancel-commands";

    /// <summary>
    /// Configurations seeded at startup — the "statically configured" path. Idempotent by name:
    /// a configuration whose name already exists is left untouched.
    /// </summary>
    public List<StaticRunConfiguration> StaticConfigurations { get; set; } = new();

    /// <summary>Validity of a Core-resolved slot credential; the workflow re-requests after expiry.</summary>
    public TimeSpan SlotCredentialLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Max slot-credential resolution requests accepted per run (keyed by resolution token) per minute.</summary>
    public int ResolutionRateLimitPermitsPerMinute { get; set; } = 60;

    /// <summary>A Core.Runner whose bus heartbeat is older than this is considered dead (failover). Mirrors PlatformHostSettings.</summary>
    public int HeartbeatTimeoutSeconds { get; set; } = 30;

    /// <summary>How often the failover monitor scans for dead runners.</summary>
    public int FailoverScanIntervalSeconds { get; set; } = 10;

    /// <summary>Re-dispatch runs orphaned by a dead Core.Runner (once per run, guarded against loops).</summary>
    public bool RedispatchOnFailover { get; set; } = true;
}

/// <summary>A run configuration provided through host settings (appsettings / environment).</summary>
public sealed class StaticRunConfiguration
{
    public string Name { get; set; } = "";
    public string WorkflowType { get; set; } = "";
    public string PackageUri { get; set; } = "";
    public Dictionary<string, string> Context { get; set; } = new();
}
