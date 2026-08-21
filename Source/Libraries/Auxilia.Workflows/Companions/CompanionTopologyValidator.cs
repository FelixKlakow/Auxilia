using System.Text.RegularExpressions;

namespace Auxilia.Workflows.Companions;

/// <summary>
/// Structural validation of a companion topology — shared by the SDK (fail-fast at schema
/// build) and the Core's registration path (refusing an invalid signed package). Returns
/// every violation; an empty list means the topology is well-formed.
/// </summary>
public static partial class CompanionTopologyValidator
{
    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex NamePattern();

    public static IReadOnlyList<string> Validate(
        IReadOnlyList<CompanionDeclaration> companions, PodControlDeclaration? podControl)
    {
        var errors = new List<string>(Validate(companions));
        if (podControl is null)
            return errors;
        if (podControl.MaxContainers < 1)
            errors.Add($"Pod control MaxContainers {podControl.MaxContainers} must be at least 1.");
        foreach (var volume in podControl.PodVolumes)
            if (string.IsNullOrWhiteSpace(volume) || !NamePattern().IsMatch(volume))
                errors.Add($"Pod-control volume name '{volume}' must match [a-z][a-z0-9-]{{0,31}}.");
        return errors;
    }

    public static IReadOnlyList<string> Validate(IReadOnlyList<CompanionDeclaration> companions)
    {
        var errors = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var companion in companions)
        {
            if (string.IsNullOrWhiteSpace(companion.Name) || !NamePattern().IsMatch(companion.Name))
                errors.Add($"Companion name '{companion.Name}' must match [a-z][a-z0-9-]{{0,31}} (it becomes a network alias).");
            else if (!names.Add(companion.Name))
                errors.Add($"Companion name '{companion.Name}' is declared more than once.");

            if (string.IsNullOrWhiteSpace(companion.Image) || !companion.Image.Contains("@sha256:", StringComparison.Ordinal))
                errors.Add($"Companion '{companion.Name}' image '{companion.Image}' must be digest-pinned (…@sha256:…).");

            if (companion.MinInstances < 0 || companion.MaxInstances < 1 || companion.MinInstances > companion.MaxInstances)
                errors.Add($"Companion '{companion.Name}' scale bounds {companion.MinInstances}..{companion.MaxInstances} are invalid.");
            if (companion.CountInput is null && companion.MinInstances != companion.MaxInstances)
                errors.Add($"Companion '{companion.Name}' has open bounds but no count input to choose within them.");

            if (companion.Readiness is { } probe
                && probe.Kind is not (CompanionReadinessProbe.Tcp or CompanionReadinessProbe.Http))
                errors.Add($"Companion '{companion.Name}' readiness kind '{probe.Kind}' is unknown.");
            if (companion.Readiness is { Port: <= 0 or > 65535 })
                errors.Add($"Companion '{companion.Name}' readiness port is out of range.");

            foreach (var volume in companion.PodVolumes)
                if (string.IsNullOrWhiteSpace(volume.Name) || !NamePattern().IsMatch(volume.Name)
                    || string.IsNullOrWhiteSpace(volume.MountPath) || !volume.MountPath.StartsWith('/'))
                    errors.Add($"Companion '{companion.Name}' pod volume '{volume.Name}' needs a well-formed name and an absolute mount path.");

            foreach (var env in companion.EnvironmentVariables)
            {
                if (string.IsNullOrWhiteSpace(env.Name))
                    errors.Add($"Companion '{companion.Name}' declares an unnamed environment variable.");
                switch (env.Kind)
                {
                    case CompanionEnvironmentVariable.Literal or CompanionEnvironmentVariable.RunSecret:
                        break;
                    case CompanionEnvironmentVariable.InstanceCount or CompanionEnvironmentVariable.InstanceEndpoints:
                        if (string.IsNullOrWhiteSpace(env.SourceCompanion))
                            errors.Add($"Companion '{companion.Name}' variable '{env.Name}' names no source companion.");
                        if (env.Kind == CompanionEnvironmentVariable.InstanceEndpoints && env.Port is null or <= 0 or > 65535)
                            errors.Add($"Companion '{companion.Name}' variable '{env.Name}' needs a valid endpoint port.");
                        break;
                    default:
                        errors.Add($"Companion '{companion.Name}' variable '{env.Name}' has unknown kind '{env.Kind}'.");
                        break;
                }
            }
        }

        foreach (var companion in companions)
        {
            foreach (var dependency in companion.StartAfter)
                if (!names.Contains(dependency))
                    errors.Add($"Companion '{companion.Name}' starts after unknown companion '{dependency}'.");
            foreach (var env in companion.EnvironmentVariables)
                if (env.SourceCompanion is { Length: > 0 } source && !names.Contains(source))
                    errors.Add($"Companion '{companion.Name}' variable '{env.Name}' references unknown companion '{source}'.");
        }

        if (FindCycle(companions) is { } cycle)
            errors.Add($"Companion start order contains a cycle: {string.Join(" -> ", cycle)}.");

        return errors;
    }

    /// <summary>Hard cap of the topology — the sum of every template's upper bound.</summary>
    public static int MaxContainers(IReadOnlyList<CompanionDeclaration> companions)
        => companions.Sum(c => c.MaxInstances);

    /// <summary>The full spawn-summary cap: declared templates plus the pod-control envelope.</summary>
    public static int MaxContainers(
        IReadOnlyList<CompanionDeclaration> companions, PodControlDeclaration? podControl)
        => MaxContainers(companions) + (podControl?.MaxContainers ?? 0);

    private static IReadOnlyList<string>? FindCycle(IReadOnlyList<CompanionDeclaration> companions)
    {
        var byName = companions
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .GroupBy(c => c.Name).ToDictionary(g => g.Key, g => g.First());
        var state = new Dictionary<string, int>(); // 0 unseen, 1 on stack, 2 done
        var stack = new List<string>();

        bool Visit(string name)
        {
            if (!byName.TryGetValue(name, out var node))
                return false;
            switch (state.GetValueOrDefault(name))
            {
                case 1: stack.Add(name); return true;
                case 2: return false;
            }
            state[name] = 1;
            stack.Add(name);
            foreach (var dependency in node.StartAfter)
                if (Visit(dependency))
                    return true;
            state[name] = 2;
            stack.RemoveAt(stack.Count - 1);
            return false;
        }

        foreach (var companion in byName.Keys)
        {
            stack.Clear();
            if (Visit(companion))
            {
                var start = stack.IndexOf(stack[^1]);
                return stack.GetRange(start, stack.Count - start);
            }
        }
        return null;
    }
}
