using System.Security.Cryptography;
using Auxilia.Workflows;
using Auxilia.Workflows.Companions;

namespace Auxilia.Core.Runner.Workflows.Pods;

/// <summary>
/// The fully resolved pod of one run: every companion instance with its final environment,
/// the run's private network, the shared pod volumes, and the facts announced to the
/// workflow container (<c>Workflow__Companion__*</c>). Produced by <see cref="PodPlanner"/>
/// before launch; materialized by <see cref="IPodHost"/>.
/// </summary>
public sealed record PodPlan(
    Guid InstanceId,
    string NetworkName,
    IReadOnlyList<PlannedCompanion> Companions,
    IReadOnlyList<PodVolumeSpec> Volumes,
    IReadOnlyDictionary<string, string> WorkflowAnnouncements);

/// <summary>One companion instance to start, in pod start order.</summary>
public sealed record PlannedCompanion(
    string TemplateName,
    string InstanceName,
    string Image,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    CompanionReadinessProbe? Readiness,
    int? MemoryMb,
    double? Cpus,
    IReadOnlyList<string> VolumeBinds)
{
    /// <summary>Entrypoint override — used by runtime spawns (pod control); null keeps the image's.</summary>
    public IReadOnlyList<string>? Command { get; init; }
}

/// <summary>A run-scoped Docker volume backing one declared pod volume.</summary>
public sealed record PodVolumeSpec(string Name, string DockerVolumeName);

/// <summary>
/// Resolves a declared companion topology into a per-run <see cref="PodPlan"/> — pure
/// (deterministic given the secret factory), so every clamping and resolution rule is
/// unit-testable without Docker. Counts come from the run's dispatch context, clamped to
/// the signed bounds; run secrets are minted once per (template, variable); placeholder
/// variables resolve against the RESOLVED instances, never the declaration.
/// </summary>
public static class PodPlanner
{
    public static PodPlan? Plan(
        IReadOnlyList<CompanionDeclaration> companions,
        IReadOnlyDictionary<string, string> context,
        Guid instanceId,
        Func<string>? secretFactory = null)
        => Plan(companions, podControl: null, context, instanceId, secretFactory);

    /// <summary>
    /// A pod-control envelope forces a plan even without declared companions: the pod
    /// network and the envelope's shared volumes must exist at launch so runtime spawns
    /// have somewhere to land and the workflow has its delivery channel.
    /// </summary>
    public static PodPlan? Plan(
        IReadOnlyList<CompanionDeclaration> companions,
        PodControlDeclaration? podControl,
        IReadOnlyDictionary<string, string> context,
        Guid instanceId,
        Func<string>? secretFactory = null)
    {
        if (companions.Count == 0 && podControl is null)
            return null;
        secretFactory ??= NewSecret;

        var counts = companions.ToDictionary(c => c.Name, c => ResolveCount(c, context));
        var instanceNames = companions.ToDictionary(
            c => c.Name,
            c => InstanceNamesOf(c, counts[c.Name]));

        // One secret per (template, variable): every instance of a scaled template shares it,
        // and the workflow gets the same value announced.
        var secrets = new Dictionary<(string Template, string Variable), string>();
        foreach (var companion in companions)
            foreach (var env in companion.EnvironmentVariables)
                if (env.Kind == CompanionEnvironmentVariable.RunSecret)
                    secrets[(companion.Name, env.Name)] = secretFactory();

        var volumes = companions
            .SelectMany(c => c.PodVolumes.Select(v => v.Name))
            .Concat(podControl?.PodVolumes ?? [])
            .Distinct(StringComparer.Ordinal)
            .Select(name => new PodVolumeSpec(name, $"auxilia-pod-{instanceId:N}-{name}"))
            .ToList();
        var volumesByName = volumes.ToDictionary(v => v.Name, StringComparer.Ordinal);

        var planned = new List<PlannedCompanion>();
        foreach (var companion in TopologicalOrder(companions))
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var variable in companion.EnvironmentVariables)
                env[variable.Name] = variable.Kind switch
                {
                    CompanionEnvironmentVariable.Literal => variable.Value ?? string.Empty,
                    CompanionEnvironmentVariable.RunSecret => secrets[(companion.Name, variable.Name)],
                    CompanionEnvironmentVariable.InstanceCount =>
                        counts.GetValueOrDefault(variable.SourceCompanion!).ToString(),
                    CompanionEnvironmentVariable.InstanceEndpoints => string.Join(',',
                        instanceNames.GetValueOrDefault(variable.SourceCompanion!, [])
                            .Select(n => $"{n}:{variable.Port}")),
                    _ => string.Empty
                };
            var binds = companion.PodVolumes
                .Select(v => $"{volumesByName[v.Name].DockerVolumeName}:{v.MountPath}")
                .ToList();
            foreach (var instanceName in instanceNames[companion.Name])
                planned.Add(new PlannedCompanion(
                    companion.Name, instanceName, companion.Image, env, companion.Readiness,
                    companion.MemoryMb, companion.Cpus, binds));
        }

        var announcements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var companion in companions)
        {
            var key = companion.Name.ToUpperInvariant();
            announcements[$"{WorkflowEnvironmentVariables.CompanionPrefix}{key}__COUNT"]
                = counts[companion.Name].ToString();
            if (companion.Readiness is { } probe)
                announcements[$"{WorkflowEnvironmentVariables.CompanionPrefix}{key}__ENDPOINTS"]
                    = string.Join(',', instanceNames[companion.Name].Select(n => $"{n}:{probe.Port}"));
            foreach (var env in companion.EnvironmentVariables)
                if (env.Kind == CompanionEnvironmentVariable.RunSecret)
                    announcements[$"{WorkflowEnvironmentVariables.CompanionPrefix}{key}__SECRET__{env.Name.ToUpperInvariant()}"]
                        = secrets[(companion.Name, env.Name)];
        }

        return new PodPlan(
            instanceId,
            $"auxilia-pod-{instanceId:N}",
            planned,
            volumes,
            announcements);
    }

    /// <summary>The instance count of one template: the count input clamped to the signed bounds.</summary>
    internal static int ResolveCount(
        CompanionDeclaration companion, IReadOnlyDictionary<string, string> context)
    {
        if (companion.CountInput is not { Length: > 0 } input)
            return companion.MaxInstances;
        var requested = context.TryGetValue(input, out var raw) && int.TryParse(raw, out var parsed)
            ? parsed
            : companion.MinInstances;
        return Math.Clamp(requested, companion.MinInstances, companion.MaxInstances);
    }

    /// <summary>Bare name for single-instance templates; <c>name-1..N</c> once the bounds allow scaling.</summary>
    internal static IReadOnlyList<string> InstanceNamesOf(CompanionDeclaration companion, int count)
        => companion.MaxInstances == 1
            ? count == 1 ? [companion.Name] : []
            : Enumerable.Range(1, count).Select(i => $"{companion.Name}-{i}").ToList();

    /// <summary>Kahn's ordering over StartAfter; the validator refused cycles long before this.</summary>
    private static IEnumerable<CompanionDeclaration> TopologicalOrder(
        IReadOnlyList<CompanionDeclaration> companions)
    {
        var remaining = companions.ToDictionary(c => c.Name);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(c => c.StartAfter.All(d => emitted.Contains(d) || !remaining.ContainsKey(d)))
                .ToList();
            if (ready.Count == 0)
                throw new InvalidOperationException("companion start order contains a cycle");
            foreach (var companion in ready)
            {
                remaining.Remove(companion.Name);
                emitted.Add(companion.Name);
                yield return companion;
            }
        }
    }

    private static string NewSecret()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
}
