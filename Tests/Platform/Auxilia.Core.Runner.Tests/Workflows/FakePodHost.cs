using Auxilia.Core.Runner.Workflows.Pods;
using Docker.DotNet;

namespace Auxilia.Core.Runner.Tests.Workflows;

/// <summary>Recording pod host: no Docker, every call captured for assertions.</summary>
public sealed class FakePodHost : IPodHost
{
    public List<PodPlan> Materialized { get; } = [];
    public List<Guid> TornDown { get; } = [];
    public List<IReadOnlySet<Guid>> Sweeps { get; } = [];
    public IReadOnlyList<CompanionLog> TeardownResult { get; set; } = [];
    public Exception? MaterializeFailure { get; set; }

    public Task MaterializeAsync(IDockerClient client, PodPlan plan, CancellationToken ct)
    {
        Materialized.Add(plan);
        return MaterializeFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }

    public Task<IReadOnlyList<CompanionLog>> TeardownAsync(Guid instanceId, CancellationToken ct = default)
    {
        TornDown.Add(instanceId);
        return Task.FromResult(TeardownResult);
    }

    public Task<int> SweepOrphanedAsync(IReadOnlySet<Guid> liveInstanceIds, CancellationToken ct = default)
    {
        Sweeps.Add(liveInstanceIds);
        return Task.FromResult(0);
    }

    public List<(Guid InstanceId, string NetworkName, PlannedCompanion Companion)> Spawned { get; } = [];
    public List<(Guid InstanceId, string InstanceName)> Stopped { get; } = [];
    public int LiveCount { get; set; }
    public Exception? SpawnFailure { get; set; }

    public Task SpawnCompanionAsync(
        Guid instanceId, string networkName, PlannedCompanion companion, CancellationToken ct = default)
    {
        if (SpawnFailure is { } failure)
            return Task.FromException(failure);
        Spawned.Add((instanceId, networkName, companion));
        LiveCount++;
        return Task.CompletedTask;
    }

    public Task<bool> StopCompanionAsync(Guid instanceId, string instanceName, CancellationToken ct = default)
    {
        var existed = Spawned.Any(s => s.InstanceId == instanceId && s.Companion.InstanceName == instanceName);
        Stopped.Add((instanceId, instanceName));
        if (existed)
            LiveCount = Math.Max(0, LiveCount - 1);
        return Task.FromResult(existed);
    }

    public Task<int> CountRuntimeCompanionsAsync(Guid instanceId, CancellationToken ct = default)
        => Task.FromResult(LiveCount);
}
