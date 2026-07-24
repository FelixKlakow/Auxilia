namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Configuration for <see cref="WorkflowDispatcher"/>.
/// </summary>
public sealed class WorkflowDispatcherSettings
{
    public string CommandQueueName      { get; set; } = "workflow.run-commands";
    public string RegistrationQueueName { get; set; } = "workflow-registration";

    /// <summary>
    /// Queue this instance listens on for workflow announcements. Must be unique per Steering
    /// Instance when several share a broker: the launching instance holds the instance token,
    /// the pending package, and the slot configurations, so it must receive the announcement.
    /// The dispatcher injects this name into every launched workflow.
    /// </summary>
    public string AnnouncementQueueName { get; set; } = "workflow.announcements";

    /// <summary>
    /// Queue for just-in-time slot activation requests. Unique per Steering Instance when
    /// several share a broker (same locality rule as the announcement queue).
    /// </summary>
    public string SlotActivationQueueName { get; set; } = "workflow-slot-activation";

    /// <summary>
    /// Queue this instance listens on for audited Resource Proxy calls (ARCHITECTURE §8).
    /// Unique per Steering Instance when several share a broker (same locality rule as the
    /// announcement queue). The dispatcher injects this name into every launched workflow.
    /// </summary>
    public string ResourceProxyQueueName { get; set; } = "workflow-resource-proxy";

    /// <summary>Validity of a delivered slot credential; the SDK re-requests after expiry.</summary>
    public TimeSpan SlotCredentialLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Platform ceiling for the network policy (ARCHITECTURE §10): when false (default),
    /// run configurations requesting <c>allow-all</c> are clamped to default-deny.
    /// </summary>
    public bool AllowAllNetworkPermitted { get; set; }

    /// <summary>Endpoints removed from every run's allowlist regardless of manifest or run config.</summary>
    public List<string> BlockedEndpoints { get; set; } = [];

    /// <summary>
    /// Directory under which each run gets its output folder ({dir}/{instanceId}). Mounted
    /// into workflow containers at /workflow-output; declared outputs found there are
    /// persisted to the artifact store when the run succeeds. Must be a path the Docker
    /// daemon can bind-mount (host path) in container mode.
    /// </summary>
    public string RunOutputDirectory { get; set; } =
        Path.Combine(Path.GetTempPath(), "auxilia-run-output");

    /// <summary>
    /// Host path of the directory mounted at <see cref="RunOutputDirectory"/> when the
    /// Steering Instance itself runs in a container. Bind-mount sources are resolved by the
    /// Docker daemon on the HOST, so a container-local <see cref="RunOutputDirectory"/> is
    /// invisible to it. When set, the dispatcher keeps creating and reading
    /// {RunOutputDirectory}/{id} (its own view) but hands {RunOutputHostDirectory}/{id}
    /// (the daemon's view of the same physical directory) to the launcher as the bind source.
    /// Null (default) means <see cref="RunOutputDirectory"/> is already a host path.
    /// </summary>
    public string? RunOutputHostDirectory { get; set; }

    /// <summary>
    /// Directory under which each run gets its repository workspace ({dir}/{instanceId:N}).
    /// Mounted into workflow containers at /workspace (ARCHITECTURE §9).
    /// </summary>
    public string WorkspaceRootDirectory { get; set; } =
        Path.Combine(Path.GetTempPath(), "auxilia-workspaces");

    /// <summary>
    /// Host path of the directory mounted at <see cref="WorkspaceRootDirectory"/> when the
    /// Steering Instance itself runs in a container — same split as
    /// <see cref="RunOutputHostDirectory"/>: the dispatcher prepares under its own view, the
    /// launcher binds the daemon's view. Null (default) means
    /// <see cref="WorkspaceRootDirectory"/> is already a host path.
    /// </summary>
    public string? WorkspaceRootHostDirectory { get; set; }

    /// <summary>
    /// Warm repository cache (ARCHITECTURE §9): one entry per clone URL, host-only,
    /// never bind-mounted into any container.
    /// </summary>
    public string WarmCacheDirectory { get; set; } =
        Path.Combine(Path.GetTempPath(), "auxilia-repo-cache");

    /// <summary>Per-(run, view) persisted item cap; items beyond it are dropped and logged.</summary>
    public long MaxViewItemsPerView { get; set; } = 10_000;

    /// <summary>
    /// When true (default), announcements and registrations must carry the one-time instance
    /// token issued at launch, and responses go only to the platform-created response queue.
    /// Disable only for trusted-operator dev scenarios where workflows start outside the dispatcher.
    /// </summary>
    public bool RequireInstanceToken { get; set; } = true;

    /// <summary>Maximum age of an issued instance token before registration is rejected.</summary>
    public TimeSpan InstanceTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// When true, RunWorkflowCommands without a <c>RequestedBy</c> principal are rejected.
    /// Default false until all entry points (dashboard, MCP, adapters) attach principals;
    /// commands that do carry a principal are always policy-checked regardless of this flag.
    /// </summary>
    public bool RequirePrincipal { get; set; }

    /// <summary>Interval for this instance's liveness heartbeat in the platform data layer.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Workflow types the operator has approved for long-living deployment. One-shot is the
    /// security default; registrations declaring a long-living lifetime are rejected unless
    /// their type is listed here (ARCHITECTURE §6).
    /// </summary>
    public List<string> ApprovedLongLivingWorkflowTypes { get; set; } = [];
}
