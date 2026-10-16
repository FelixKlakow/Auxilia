namespace Auxilia.Core.Runner.Workflows;

/// <summary>
/// Configuration for <see cref="DockerWorkflowLauncher"/>.
/// All fields that need to differ between CI and local dev live here so the
/// launcher itself stays environment-agnostic.
/// </summary>
public sealed class DockerWorkflowLauncherSettings
{
    /// <summary>
    /// The operator's egress network: where each run's egress gateway reaches the outside world
    /// and the broker (<see cref="RabbitMqHost"/>), and — in "container-network" terminal mode —
    /// where the Core reaches a run's terminal. Workflow containers are NEVER attached to it:
    /// they sit on their run's own internal network behind the gateway. Null = Docker's default
    /// bridge.
    /// </summary>
    public string? NetworkName { get; set; }

    /// <summary>
    /// Image of the per-run egress gateway (<c>Source/Platform/Auxilia.EgressGateway</c>): the
    /// allowlisting, logging HTTP(S) proxy and platform relay every workflow container's
    /// traffic leaves through.
    /// </summary>
    public string EgressGatewayImage { get; set; } = "auxilia-egress-gateway:latest";

    /// <summary>How long a run's egress gateway may take to listen before the launch fails.</summary>
    public int EgressGatewayStartupSeconds { get; set; } = 120;

    /// <summary>
    /// RabbitMQ hostname as seen from the egress network (e.g. the Testcontainer network alias
    /// "rabbitmq", or host.docker.internal for a broker on the host). Workflow containers reach
    /// it only through their gateway's relay.
    /// </summary>
    public string RabbitMqHost { get; set; } = "localhost";

    /// <remarks>
    /// There is deliberately no user name or password here: every workflow container connects
    /// with its OWN per-instance broker identity, minted at dispatch (<see cref="WorkloadBusCredentials"/>).
    /// </remarks>
    public int RabbitMqPort { get; set; } = 5672;

    /// <summary>
    /// Process-count ceiling per workflow container (fork-bomb containment). Zero or negative
    /// disables the limit.
    /// </summary>
    public long ContainerPidsLimit { get; set; } = 4096;

    /// <summary>
    /// Memory ceiling per workflow container in MiB, so one run cannot starve the runner host.
    /// Zero or negative disables the limit.
    /// </summary>
    public long ContainerMemoryMb { get; set; } = 8192;

    /// <summary>
    /// The Linux capabilities a workflow container keeps after <c>--cap-drop ALL</c>. Null = the
    /// platform's minimal set (<see cref="DockerWorkflowLauncher.RetainedCapabilities"/>: workspace
    /// ownership, the drop to the unprivileged agent user, signalling its own children). A host
    /// with special workloads may widen it deliberately; it never grants privileged mode.
    /// </summary>
    public string[]? ContainerCapabilities { get; set; }

    /// <summary>Lines of the workflow container's own output kept as the <c>container-log</c> run artifact.</summary>
    public int ContainerLogLines { get; set; } = 20000;

    /// <summary>CPU ceiling per workflow container (fractional cores). Zero or negative = unlimited.</summary>
    public double ContainerCpus { get; set; }

    /// <summary>
    /// OCI runtime for workflow containers, e.g. <c>runsc</c> (gVisor) or <c>kata-runtime</c> for a
    /// second kernel boundary around agent workloads. Null = the daemon default (runc). The
    /// runtime must be installed on the Docker host.
    /// </summary>
    public string? ContainerRuntime { get; set; }

    /// <summary>
    /// Sandboxed OCI runtimes chosen automatically when <see cref="ContainerRuntime"/> is unset —
    /// the first one the Docker daemon offers wraps every workflow container and companion in a
    /// second kernel boundary (defaults in appsettings: gVisor <c>runsc</c>, Kata).
    /// </summary>
    public string[] PreferredSandboxRuntimes { get; set; } = [];

    /// <summary>
    /// Refuse every launch that would run on the shared host kernel: no configured runtime and
    /// none of <see cref="PreferredSandboxRuntimes"/> offered by the daemon. Unset follows the
    /// runner's posture — required as soon as <c>WorkflowDispatcher:TrustedSigningKeys</c> is
    /// configured (production); only an explicit <c>false</c> opts a production runner out.
    /// </summary>
    public bool? RequireSandboxRuntime { get; set; }

    /// <summary>
    /// Platform service endpoints (<c>host:port</c>) no run may reach directly — e.g. the broker's
    /// management API, the Core, a database — refused by every run's egress gateway even when an
    /// allow entry names the host. The broker's AMQP endpoint is always protected.
    /// </summary>
    public string[] ProtectedEndpoints { get; set; } = [];

    /// <summary>
    /// Operator labels stamped on every container, network and volume this runner creates
    /// (ownership, cost centre, a test session), so tooling selects exactly this runner's objects.
    /// Platform labels win on a key collision.
    /// </summary>
    public Dictionary<string, string> ContainerLabels { get; set; } = [];

    internal Dictionary<string, string> Stamp(Dictionary<string, string> platformLabels)
    {
        foreach (var (key, value) in ContainerLabels)
            platformLabels.TryAdd(key, value);
        return platformLabels;
    }

    /// <summary>Resolves the posture-following defaults against the dispatcher's trust configuration.</summary>
    public void ApplyPosture(WorkflowDispatcherSettings dispatcher)
        => RequireSandboxRuntime ??= dispatcher.TrustedSigningKeys.Count > 0;

    /// <summary>
    /// A daemon that cannot enforce egress (Windows containers) runs NOTHING unless this is set —
    /// the operator's explicit acceptance that its allow-all runs leave the network unmediated.
    /// </summary>
    public bool AllowUnenforcedEgress { get; set; }

    /// <summary>
    /// URI of the Docker daemon socket used to launch workflow containers. Defaults to the
    /// host OS's standard local daemon endpoint (named pipe on Windows, Unix socket elsewhere).
    /// Override in config or env for remote daemons.
    /// </summary>
    public string DockerSocketPath { get; set; } = OperatingSystem.IsWindows()
        ? "npipe://./pipe/docker_engine"
        : "unix:///var/run/docker.sock";

    /// <summary>
    /// Base container image used to run workflow assemblies.
    /// The extracted package is bind-mounted over the container filesystem.
    /// </summary>
    public string RuntimeImage { get; set; } = "mcr.microsoft.com/dotnet/runtime:8.0";

    /// <summary>
    /// Maps ProviderType → absolute path to the *.slothandler.dll on the Core.Runner filesystem.
    /// The matching *.slothandler.manifest.json is always co-located with the DLL.
    /// </summary>
    public Dictionary<string, string> SlotPackages { get; set; } = new();

    /// <summary>
    /// Re-adopt the previous runner process's workflow containers at startup: re-attach exit
    /// watchers to running ones, collect the real exit of ones that died in the downtime, and
    /// clean-kill only what cannot be matched to a live run (single-runner-per-host assumption).
    /// </summary>
    public bool ReadoptContainersOnStart { get; set; } = true;

    /// <summary>
    /// Composed environment images (<c>auxilia-env:&lt;hash&gt;</c>) older than this and not used
    /// by any container are removed at startup. Content-addressing makes the sweep safe — a
    /// purged composition that is needed again rebuilds on the next launch. Zero or negative
    /// disables the sweep.
    /// </summary>
    public double ComposedImageMaxAgeDays { get; set; } = 14;

    /// <summary>
    /// Maps an environment-capability provider type → absolute path of its Dockerfile fragment
    /// on the Core.Runner filesystem. This is the runner-owned side of the environment catalog:
    /// the fragment is layered onto the workflow image when a run selects the capability.
    /// </summary>
    public Dictionary<string, string> EnvironmentLayers { get; set; } = new();

    /// <summary>
    /// Additional environment variables injected into every workflow container launch.
    /// For example: <c>{"AUXILIA_DEVELOPER_MODE": "1"}</c>.
    /// </summary>
    public Dictionary<string, string>? ExtraEnvironmentVariables { get; set; }

    /// <summary>
    /// Adds <c>host.docker.internal:host-gateway</c> to every workflow container so the name
    /// resolves on PLAIN Linux Docker exactly like it does under Docker Desktop (where the
    /// name resolves without it). Off by default — least privilege: enable it only on a plain
    /// Linux dev host whose RabbitMQ/clone URLs point at the host. Grants no connectivity by
    /// itself; the run's network policy still governs egress.
    /// </summary>
    public bool MapHostGateway { get; set; }

    /// <summary>
    /// How a declared interactive terminal is made reachable for the Core's proxy.
    /// "loopback" (default) publishes the container port to an ephemeral 127.0.0.1 host port —
    /// right when Core.Api runs as a host process beside this runner. "container-network"
    /// publishes no host port; the endpoint is the container name on the shared Docker network —
    /// right when the Core itself is containerized. End clients never reach either directly.
    /// </summary>
    public string TerminalPublishMode { get; set; } = "loopback";
}


