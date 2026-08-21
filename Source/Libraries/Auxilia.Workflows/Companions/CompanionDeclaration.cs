namespace Auxilia.Workflows.Companions;

/// <summary>
/// A companion container this workflow declares for its per-run pod (see
/// docs/test-fabric-and-swarm-design.md §A): an inert service — database, broker, simulated
/// machine — materialized by the runner on the run's private pod network before the workflow
/// starts, and torn down with the run. Part of the signed manifest; the image must be
/// digest-pinned so an approved topology cannot be repointed.
/// </summary>
public sealed record CompanionDeclaration(string Name, string Image)
{
    public IReadOnlyList<CompanionEnvironmentVariable> EnvironmentVariables { get; init; } = [];

    /// <summary>Readiness gate consulted before dependents (and finally the workflow) start.</summary>
    public CompanionReadinessProbe? Readiness { get; init; }

    public int? MemoryMb { get; init; }

    public double? Cpus { get; init; }

    /// <summary>Lower scale bound; 0 makes the whole template optional per run.</summary>
    public int MinInstances { get; init; } = 1;

    public int MaxInstances { get; init; } = 1;

    /// <summary>
    /// Run input whose value picks the instance count within the declared bounds — the
    /// declared-bounds/per-run-choice split of the design. Null pins the count to
    /// <see cref="MaxInstances"/> (which must then equal <see cref="MinInstances"/>).
    /// </summary>
    public string? CountInput { get; init; }

    /// <summary>Companions that must be running and ready before this one starts.</summary>
    public IReadOnlyList<string> StartAfter { get; init; } = [];

    /// <summary>Run-scoped shared volumes mounted into this companion (and the workflow).</summary>
    public IReadOnlyList<CompanionPodVolume> PodVolumes { get; init; } = [];
}

/// <summary>
/// One environment variable of a companion. <see cref="Kind"/> decides how the runner
/// resolves the value before the pod starts; literals aside, values exist only per run.
/// </summary>
public sealed record CompanionEnvironmentVariable(string Name, string Kind)
{
    public const string Literal = "literal";

    /// <summary>A cryptographically random value minted per run, announced to the workflow.</summary>
    public const string RunSecret = "run-secret";

    /// <summary>The resolved instance count of <see cref="SourceCompanion"/>.</summary>
    public const string InstanceCount = "instance-count";

    /// <summary>Comma-joined <c>host:port</c> list over the resolved instances of <see cref="SourceCompanion"/>.</summary>
    public const string InstanceEndpoints = "instance-endpoints";

    public string? Value { get; init; }

    public string? SourceCompanion { get; init; }

    public int? Port { get; init; }
}

/// <summary>Readiness gate of a companion; kinds: <see cref="Tcp"/>, <see cref="Http"/>.</summary>
public sealed record CompanionReadinessProbe(string Kind, int Port)
{
    public const string Tcp = "tcp";
    public const string Http = "http";

    /// <summary>Path probed on <see cref="Http"/> probes; any 2xx counts as ready.</summary>
    public string? HttpPath { get; init; }

    public int TimeoutSeconds { get; init; } = 120;
}

/// <summary>
/// A run-scoped scratch volume shared between the naming companions and the workflow
/// container (mounted there under <c>/workspace/pod/&lt;Name&gt;</c>) — for files stdout
/// capture cannot reach. Created empty per run, deleted with the run.
/// </summary>
public sealed record CompanionPodVolume(string Name, string MountPath);
