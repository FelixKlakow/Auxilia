using Auxilia.Workflows.Companions;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace Auxilia.Core.Runner.Workflows.Pods;

/// <summary>A torn-down companion's identity and captured log tail (for artifact persistence).</summary>
public sealed record CompanionLog(string InstanceName, string? LogTail);

/// <summary>
/// Materializes and tears down the run's pod (design: test-fabric-and-swarm §A). Companions
/// live on a private per-run <c>--internal</c> network with zero egress; the workflow
/// container is connected to it additionally, staying the pod's only governed path outside.
/// </summary>
public interface IPodHost
{
    /// <summary>
    /// Starts the pod on the run's private network, in declared start order, each companion
    /// gated on its readiness. Throws on any failure — the caller tears down and fails the run.
    /// </summary>
    Task MaterializeAsync(IDockerClient client, PodPlan plan, CancellationToken ct);

    /// <summary>
    /// Removes every pod remnant of a run — containers (capturing their log tails), the
    /// network, the volumes. Label-driven and idempotent: safe on runs that never had a pod.
    /// </summary>
    Task<IReadOnlyList<CompanionLog>> TeardownAsync(Guid instanceId, CancellationToken ct = default);

    /// <summary>Tears down every pod whose instance is not in <paramref name="liveInstanceIds"/> (startup sweep).</summary>
    Task<int> SweepOrphanedAsync(IReadOnlySet<Guid> liveInstanceIds, CancellationToken ct = default);

    /// <summary>Spawns ONE companion at runtime (pod control) onto the run's existing pod network.</summary>
    Task SpawnCompanionAsync(
        Guid instanceId, string networkName, PlannedCompanion companion, CancellationToken ct = default);

    /// <summary>Stops and removes one runtime-spawned companion; false when it does not exist.</summary>
    Task<bool> StopCompanionAsync(Guid instanceId, string instanceName, CancellationToken ct = default);

    /// <summary>
    /// The number of runtime-spawned pod containers of a run — the envelope's clamp input.
    /// Declared companion templates are separately signed topology and never count here.
    /// </summary>
    Task<int> CountRuntimeCompanionsAsync(Guid instanceId, CancellationToken ct = default);
}

/// <summary>Waits until a started companion container counts as ready.</summary>
public interface ICompanionReadinessChecker
{
    Task WaitUntilReadyAsync(
        IDockerClient client, string containerId, string companionName,
        CompanionReadinessProbe? probe, CancellationToken ct);
}

/// <summary>
/// Inspect-based readiness: the container must reach Running (and, when its image defines a
/// Docker health check, "healthy") within the probe's timeout; a container that dies while
/// waiting fails immediately. The declared TCP/HTTP probe target is recorded topology (it
/// rides the announcements) — in-network dialing needs a prober on the pod network and is a
/// documented follow-up; crash-on-start and health-checked images already fail fast here.
/// </summary>
public sealed class InspectCompanionReadinessChecker : ICompanionReadinessChecker
{
    public async Task WaitUntilReadyAsync(
        IDockerClient client, string containerId, string companionName,
        CompanionReadinessProbe? probe, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(probe?.TimeoutSeconds ?? 30);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var inspection = await client.Containers.InspectContainerAsync(containerId, ct);
            var state = inspection.State;
            if (state is { Running: false, Status: not "created" })
                throw new InvalidOperationException(
                    $"companion '{companionName}' exited during startup (status {state?.Status}, exit code {state?.ExitCode})");
            var health = state?.Health?.Status;
            if (state is { Running: true } && health is null or "healthy")
                return;
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException(
                    $"companion '{companionName}' did not become ready within {timeout.TotalSeconds:0}s"
                    + (health is null ? "" : $" (health: {health})"));
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }
}

public sealed class DockerPodHost(
    IOptions<DockerWorkflowLauncherSettings> settingsOptions,
    IDockerClientFactory clientFactory,
    ICompanionReadinessChecker readinessChecker,
    ILogger<DockerPodHost> logger) : IPodHost
{
    internal const string CompanionLabel = "auxilia.companion";
    internal const string CompanionNameLabel = "auxilia.companion-name";
    internal const string RuntimeSpawnLabel = "auxilia.companion-runtime";

    public async Task MaterializeAsync(IDockerClient client, PodPlan plan, CancellationToken ct)
    {
        // The pod network is --internal: companions have ZERO egress by design. The workflow
        // container is connected additionally by the launcher and keeps its own governed path.
        await client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = plan.NetworkName,
            Internal = true,
            Labels = PodLabels(plan.InstanceId)
        }, ct);

        foreach (var volume in plan.Volumes)
            await client.Volumes.CreateAsync(new VolumesCreateParameters
            {
                Name = volume.DockerVolumeName,
                Labels = PodLabels(plan.InstanceId)
            }, ct);

        foreach (var companion in plan.Companions)
        {
            await EnsureImageAsync(client, companion.Image, ct);
            var created = await client.Containers.CreateContainerAsync(
                BuildCompanionParameters(companion, plan.InstanceId, plan.NetworkName), ct);
            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
            logger.LogInformation(
                "Companion started. Instance={InstanceId} Companion={Companion} ContainerId={ContainerId}",
                plan.InstanceId, companion.InstanceName, created.ID[..Math.Min(12, created.ID.Length)]);
            // Fail-fast, in order: a dependent template's containers only start once this one
            // is ready — the same contract repository setup scripts follow.
            await readinessChecker.WaitUntilReadyAsync(
                client, created.ID, companion.InstanceName, companion.Readiness, ct);
        }
    }

    /// <summary>Extracted static so unit tests verify parameter construction without a daemon.</summary>
    internal static CreateContainerParameters BuildCompanionParameters(
        PlannedCompanion companion, Guid instanceId, string networkName, bool runtimeSpawned = false)
    {
        var labels = PodLabels(instanceId);
        labels[CompanionNameLabel] = companion.InstanceName;
        if (runtimeSpawned)
            labels[RuntimeSpawnLabel] = "1";
        return new CreateContainerParameters
        {
            Image = companion.Image,
            Name = $"auxilia-pod-{instanceId:N}-{companion.InstanceName}",
            Cmd = companion.Command is { Count: > 0 } command ? command.ToList() : null,
            Env = companion.EnvironmentVariables.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
            Labels = labels,
            HostConfig = new HostConfig
            {
                Binds = companion.VolumeBinds.ToList(),
                Memory = companion.MemoryMb is { } mb ? mb * 1024L * 1024L : 0,
                NanoCPUs = companion.Cpus is { } cpus ? (long)(cpus * 1_000_000_000) : 0
            },
            NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [networkName] = new() { Aliases = [companion.InstanceName] }
                }
            }
        };
    }

    public async Task SpawnCompanionAsync(
        Guid instanceId, string networkName, PlannedCompanion companion, CancellationToken ct = default)
    {
        using var client = clientFactory.CreateClient(settingsOptions.Value.DockerSocketPath);
        await EnsureImageAsync(client, companion.Image, ct);
        var created = await client.Containers.CreateContainerAsync(
            BuildCompanionParameters(companion, instanceId, networkName, runtimeSpawned: true), ct);
        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        logger.LogInformation(
            "Runtime companion started. Instance={InstanceId} Companion={Companion} ContainerId={ContainerId}",
            instanceId, companion.InstanceName, created.ID[..Math.Min(12, created.ID.Length)]);
        await readinessChecker.WaitUntilReadyAsync(
            client, created.ID, companion.InstanceName, companion.Readiness, ct);
    }

    public async Task<bool> StopCompanionAsync(
        Guid instanceId, string instanceName, CancellationToken ct = default)
    {
        using var client = clientFactory.CreateClient(settingsOptions.Value.DockerSocketPath);
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = LabelFilter(
                $"{CompanionLabel}=1",
                $"{RuntimeSpawnLabel}=1",
                $"{DockerWorkflowLauncher.InstanceIdLabel}={instanceId:D}",
                $"{CompanionNameLabel}={instanceName}")
        }, ct);
        var found = false;
        foreach (var container in containers)
        {
            found = true;
            try
            {
                await client.Containers.RemoveContainerAsync(
                    container.ID, new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (DockerContainerNotFoundException)
            {
                // Already gone — fine.
            }
        }
        return found;
    }

    public async Task<int> CountRuntimeCompanionsAsync(Guid instanceId, CancellationToken ct = default)
    {
        using var client = clientFactory.CreateClient(settingsOptions.Value.DockerSocketPath);
        // All = true on purpose: a runtime companion that crashed still exists (and holds its
        // name) until an explicit stop removes it, so it keeps occupying its envelope slot.
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = LabelFilter(
                $"{CompanionLabel}=1",
                $"{RuntimeSpawnLabel}=1",
                $"{DockerWorkflowLauncher.InstanceIdLabel}={instanceId:D}")
        }, ct);
        return containers.Count;
    }

    private static Dictionary<string, string> PodLabels(Guid instanceId) => new()
    {
        [CompanionLabel] = "1",
        [DockerWorkflowLauncher.InstanceIdLabel] = instanceId.ToString("D")
    };

    private async Task EnsureImageAsync(IDockerClient client, string image, CancellationToken ct)
    {
        try
        {
            await client.Images.InspectImageAsync(image, ct);
            return;
        }
        catch (DockerImageNotFoundException)
        {
            // fall through to pull
        }
        logger.LogInformation("Pulling companion image {Image}.", image);
        string? pullError = null;
        var progress = new Progress<JSONMessage>(m =>
        {
            if (m.ErrorMessage is { Length: > 0 } error)
                pullError = error;
        });
        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = image }, null, progress, ct);
        if (pullError is not null)
            throw new InvalidOperationException($"companion image pull failed: {pullError}");
        await client.Images.InspectImageAsync(image, ct);
    }

    public async Task<IReadOnlyList<CompanionLog>> TeardownAsync(
        Guid instanceId, CancellationToken ct = default)
    {
        using var client = clientFactory.CreateClient(settingsOptions.Value.DockerSocketPath);
        var logs = new List<CompanionLog>();

        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = LabelFilter(
                $"{CompanionLabel}=1",
                $"{DockerWorkflowLauncher.InstanceIdLabel}={instanceId:D}")
        }, ct);
        foreach (var container in containers)
        {
            var name = container.Labels is not null
                       && container.Labels.TryGetValue(CompanionNameLabel, out var labeled)
                ? labeled
                : container.ID[..Math.Min(12, container.ID.Length)];
            logs.Add(new CompanionLog(name, await TryCaptureLogTailAsync(client, container.ID, ct)));
            try
            {
                await client.Containers.RemoveContainerAsync(
                    container.ID, new ContainerRemoveParameters { Force = true }, ct);
            }
            catch (DockerContainerNotFoundException)
            {
                // Already gone — fine.
            }
        }

        var volumes = await client.Volumes.ListAsync(new VolumesListParameters
        {
            Filters = LabelFilter($"{DockerWorkflowLauncher.InstanceIdLabel}={instanceId:D}")
        }, ct);
        foreach (var volume in volumes.Volumes ?? [])
            await TryIgnoreNotFoundAsync(() => client.Volumes.RemoveAsync(volume.Name, force: true, ct));

        var networks = await client.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = LabelFilter($"{DockerWorkflowLauncher.InstanceIdLabel}={instanceId:D}")
        }, ct);
        foreach (var network in networks.Where(n => n.Labels?.ContainsKey(CompanionLabel) == true))
            await TryIgnoreNotFoundAsync(() => client.Networks.DeleteNetworkAsync(network.ID, ct));

        if (logs.Count > 0)
            logger.LogInformation(
                "Pod torn down. Instance={InstanceId} Companions={Count}", instanceId, logs.Count);
        return logs;
    }

    public async Task<int> SweepOrphanedAsync(
        IReadOnlySet<Guid> liveInstanceIds, CancellationToken ct = default)
    {
        using var client = clientFactory.CreateClient(settingsOptions.Value.DockerSocketPath);
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = LabelFilter($"{CompanionLabel}=1")
        }, ct);
        var orphaned = containers
            .Select(c => c.Labels is not null
                         && c.Labels.TryGetValue(DockerWorkflowLauncher.InstanceIdLabel, out var raw)
                         && Guid.TryParse(raw, out var id) ? id : (Guid?)null)
            .Where(id => id is { } value && !liveInstanceIds.Contains(value))
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        foreach (var instanceId in orphaned)
        {
            logger.LogWarning("Sweeping orphaned pod of instance {InstanceId}.", instanceId);
            await TeardownAsync(instanceId, ct);
        }
        return orphaned.Count;
    }

    private static async Task<string?> TryCaptureLogTailAsync(
        IDockerClient client, string containerId, CancellationToken ct)
    {
        try
        {
            using var logs = await client.Containers.GetContainerLogsAsync(
                containerId, tty: false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Tail = "400" }, ct);
            using var stdout = new MemoryStream();
            using var stderr = new MemoryStream();
            await logs.CopyOutputToAsync(Stream.Null, stdout, stderr, ct);
            return (System.Text.Encoding.UTF8.GetString(stdout.ToArray()) + "\n"
                    + System.Text.Encoding.UTF8.GetString(stderr.ToArray())).Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task TryIgnoreNotFoundAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DockerApiException)
        {
            // Already gone, or still winding down — the next sweep catches stragglers.
        }
    }

    private static Dictionary<string, IDictionary<string, bool>> LabelFilter(params string[] labels)
        => new()
        {
            ["label"] = labels.ToDictionary(l => l, _ => true)
        };
}
