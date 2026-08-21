namespace Auxilia.Workflows.Companions;

/// <summary>
/// The pod-control ENVELOPE a workflow declares (run-pod design §"pod controller"): the
/// signed permission to spawn companions at runtime, bounded by <see cref="MaxContainers"/>.
/// The manifest never names images — which bases a run may spawn from is pinned by its
/// CONFIGURATION (dispatch context key <c>pod-bases</c>) and resolved against the Core's
/// digest-pinned environment-base catalog at dispatch.
/// </summary>
public sealed record PodControlDeclaration(int MaxContainers, string? Description = null)
{
    /// <summary>
    /// Run-scoped shared volumes created at launch and mounted into the workflow container
    /// under <c>/workspace/pod/&lt;name&gt;</c> — the software-delivery channel: the workflow
    /// stages binaries there and spawn specs mount them by name.
    /// </summary>
    public IReadOnlyList<string> PodVolumes { get; init; } = [];
}

/// <summary>One runtime spawn request: what to start, from which configured base.</summary>
public sealed record CompanionSpec(string Name, string Base)
{
    /// <summary>Container entrypoint override — typically a binary staged on a pod volume.</summary>
    public IReadOnlyList<string>? Command { get; init; }

    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    public CompanionReadinessProbe? Readiness { get; init; }

    public int? MemoryMb { get; init; }

    public double? Cpus { get; init; }

    /// <summary>Declared pod-volume name → mount path inside the spawned companion.</summary>
    public IReadOnlyDictionary<string, string>? VolumeMounts { get; init; }
}

/// <summary>A spawned companion as the platform materialized it.</summary>
public sealed record SpawnedCompanion(string Name, string? Endpoint);

/// <summary>
/// Runtime pod control, delivered via DI when the manifest declares
/// <see cref="PodControlDeclaration"/>: spawn and stop companions on the run's private pod
/// network — runner-mediated, audited, clamped to the signed envelope and the
/// configuration-pinned base set. The container never sees a Docker socket.
/// </summary>
public interface IPodController
{
    /// <summary>Spawns one companion; throws when refused (unknown base, envelope exceeded).</summary>
    Task<SpawnedCompanion> SpawnAsync(CompanionSpec spec, CancellationToken ct = default);

    /// <summary>Stops and removes one previously spawned companion by its name.</summary>
    Task StopAsync(string name, CancellationToken ct = default);
}
