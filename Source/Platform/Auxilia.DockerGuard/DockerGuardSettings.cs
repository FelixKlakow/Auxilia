namespace Auxilia.DockerGuard;

/// <summary>
/// What the guard lets a runner do with the Docker daemon — all data, no knowledge of
/// workflows or providers. Bound from the <c>DockerGuard</c> configuration section.
/// </summary>
public sealed class DockerGuardSettings
{
    /// <summary>The real daemon: <c>unix:///var/run/docker.sock</c>, <c>npipe://./pipe/docker_engine</c> or <c>tcp://host:port</c>.</summary>
    public string Upstream { get; set; } = OperatingSystem.IsWindows()
        ? "npipe://./pipe/docker_engine"
        : "unix:///var/run/docker.sock";

    /// <summary>
    /// The labels (<c>key=value</c>) that mark a container, network or volume as an Auxilia
    /// runner's own: everything the runner creates must carry one, and it may act on existing
    /// objects only when they carry one.
    /// </summary>
    public string[] OwnershipLabels { get; set; } = ["auxilia.workflow=1", "auxilia.egress=1", "auxilia.companion=1"];

    /// <summary>
    /// Host directories (the daemon's view, absolute paths) below which a container may bind a
    /// directory: the runner's workspace, run-output and package roots. The guard carries every
    /// such bind out as a subpath mount of a volume that binds the root, so the DAEMON resolves
    /// the path beneath the root — a symlink a compromised runner planted there cannot lead out,
    /// and nothing can swap it in between (Docker 26 / Engine API 1.45 or newer). Empty = no host
    /// bind at all.
    /// </summary>
    public string[] BindRoots { get; set; } = [];

    /// <summary>
    /// Labels stamped on the volumes the guard keeps for its <see cref="BindRoots"/> (an operator
    /// or test-session label), so tooling can select them. Never an ownership label: the runner
    /// cannot name, mount or remove a root volume itself.
    /// </summary>
    public Dictionary<string, string> RootVolumeLabels { get; set; } = [];

    /// <summary>Networks a runner's container may join besides the runner's own: the operator's egress network.</summary>
    public string[] OutboundNetworks { get; set; } = ["bridge"];

    /// <summary>
    /// The capabilities a runner's container may END UP with: Docker's default set minus its
    /// <c>CapDrop</c> plus its <c>CapAdd</c> must stay within these. The default covers the
    /// runner's workflow containers (drop ALL, keep six) and companions (Docker's defaults minus
    /// raw sockets, device nodes, chroot and capability juggling); a container that drops
    /// nothing keeps NET_RAW, MKNOD… and is refused.
    /// </summary>
    public string[] AllowedCapabilities { get; set; } =
        ["CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "SETUID", "SETGID", "KILL", "NET_BIND_SERVICE"];

    /// <summary>
    /// The OCI runtimes a container may name — the runner's sandbox runtimes (e.g. <c>runsc</c>).
    /// Empty = the daemon's default runtime only. When set, every container must name one of
    /// them, so a container cannot fall back to the shared host kernel by naming runc or by
    /// naming nothing (and no runtime whose hooks inject devices is ever chosen).
    /// </summary>
    public string[] Runtimes { get; set; } = [];

    /// <summary>The only image tags the runner may build and delete: its composed environments.</summary>
    public string BuildTagPrefix { get; set; } = "auxilia-env:";

    /// <summary>
    /// The oldest Engine API version a request may name in its path. Unversioned requests (the
    /// runner's) get the daemon's current semantics; older versions carry compatibility
    /// behaviour the policy does not model.
    /// </summary>
    public string MinimumApiVersion { get; set; } = "1.45";

    /// <summary>Upper bound for a JSON request body the guard inspects (create/connect requests).</summary>
    public int MaxInspectedBodyBytes { get; set; } = 1 << 20;
}
