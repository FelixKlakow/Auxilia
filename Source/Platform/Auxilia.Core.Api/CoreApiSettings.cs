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

    /// <summary>
    /// A run still Dispatched (no runner ever claimed the command) after this many seconds is
    /// failed over as <c>dispatch-never-claimed</c>. Ignored under <see cref="AllowDispatchWithoutRunner"/>.
    /// </summary>
    public int DispatchClaimTimeoutSeconds { get; set; } = 120;

    /// <summary>Days a dispatch's resolution record (the rerun context) is retained; 0 keeps forever.</summary>
    public int ResolutionRecordRetentionDays { get; set; } = 30;

    /// <summary>
    /// When false (default), a dispatch is rejected with a clear error while no live Core.Runner
    /// heartbeat is known — a run nobody can execute would otherwise queue silently. True restores
    /// pure queue-until-a-runner-arrives semantics (test rigs, deliberate buffering deployments).
    /// </summary>
    public bool AllowDispatchWithoutRunner { get; set; }

    /// <summary>
    /// Workflow types seeded into the registry at startup as Active — the operator-controlled
    /// static path (host configuration IS the operator's trust decision). Idempotent by type name.
    /// </summary>
    public List<StaticWorkflowType> StaticWorkflowTypes { get; set; } = new();

    /// <summary>
    /// SPKI public keys (base64) of trusted workflow publishers. A registered package signed by
    /// one of these activates immediately; any other signature enters the approval queue.
    /// </summary>
    public List<string> TrustedPublisherKeys { get; set; } = new();

    /// <summary>Directory where packages transferred to the Core for signing/serving are stored.</summary>
    public string PackageStoreDirectory { get; set; } = "./core-data/packages";

    /// <summary>
    /// Externally reachable base address of this Core API (as seen from the runner) — used to
    /// rewrite <c>core://</c> package coordinates into downloadable URLs at dispatch. Unset means
    /// Core-stored packages cannot be dispatched.
    /// </summary>
    public string? PublicBaseAddress { get; set; }

    /// <summary>
    /// PEM file with the Core's RSA signing keypair (the platform signing authority). When set,
    /// approving a pending package re-signs it with this key so the platform key becomes the
    /// publisher of record. Unset means approval activates the package as-signed.
    /// </summary>
    public string? SigningKeyPemFile { get; set; }

    /// <summary>
    /// Approval-pipeline handler names (from the registered handler catalog) consulted, in order,
    /// when a registration enters Pending — e.g. an email notifier or an AI safety-check workflow.
    /// </summary>
    public List<string> ApprovalHandlers { get; set; } = new();

    /// <summary>SMTP settings of the email approval notifier; unconfigured means it silently defers.</summary>
    public ApprovalEmailSettings ApprovalEmail { get; set; } = new();

    /// <summary>Per-run cap on Core-persisted view items; items beyond it are dropped and logged.</summary>
    public int MaxPersistedViewItemsPerRun { get; set; } = 1000;

    /// <summary>
    /// Quiet-period after which the SSE streams emit a <c>: ping</c> comment so clients can tell
    /// an idle stream from a dead connection. 0 disables keepalives.
    /// </summary>
    public int SseKeepaliveSeconds { get; set; } = 15;
}

/// <summary>Where the email approval handler notifies the signing authority.</summary>
public sealed class ApprovalEmailSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>A workflow type provided through host settings, registered as Active at startup.</summary>
public sealed class StaticWorkflowType
{
    public string WorkflowType { get; set; } = "";
    public string PackageUri { get; set; } = "";

    /// <summary>Optional schema JSON (serialized <c>WorkflowSchema</c>) so editors work before the first run.</summary>
    public string? SchemaJson { get; set; }
}

/// <summary>A run configuration provided through host settings (appsettings / environment).</summary>
public sealed class StaticRunConfiguration
{
    public string Name { get; set; } = "";
    public string WorkflowType { get; set; } = "";
    public Dictionary<string, string> Context { get; set; } = new();
}
