using System.Collections.Concurrent;

namespace Auxilia.Core.Api.Services;

/// <summary>What the Core knows about one runner, purely from its bus heartbeats.</summary>
public sealed record RunnerLiveness(
    Guid ServiceId,
    string? ServiceName,
    DateTimeOffset LastSeen,
    string? HostPlatform,
    string? HostArchitecture);

/// <summary>
/// Tracks the last time each Core.Runner was heard from over the bus (via <c>RunnerHeartbeat</c>)
/// plus what the beat advertises (service name, host platform). Pure in-memory liveness state — it
/// never reads any database, so it respects the Core/Runner DB isolation. A runner is "dead" once
/// its last beat is older than the failover cutoff.
/// </summary>
public sealed class RunnerLivenessTracker
{
    private readonly ConcurrentDictionary<Guid, RunnerLiveness> _runners = new();

    /// <summary>
    /// Records that <paramref name="serviceId"/> was seen at <paramref name="at"/> (keeps the
    /// latest beat time; identity and platform keep the last non-null advertisement).
    /// </summary>
    public void Record(
        Guid serviceId, DateTimeOffset at,
        string? serviceName = null, string? hostPlatform = null, string? hostArchitecture = null)
        => _runners.AddOrUpdate(
            serviceId,
            new RunnerLiveness(serviceId, serviceName, at, hostPlatform, hostArchitecture),
            (_, existing) => new RunnerLiveness(
                serviceId,
                serviceName ?? existing.ServiceName,
                at > existing.LastSeen ? at : existing.LastSeen,
                hostPlatform ?? existing.HostPlatform,
                hostArchitecture ?? existing.HostArchitecture));

    /// <summary>Service ids whose last beat is strictly older than <paramref name="cutoff"/>.</summary>
    public IReadOnlyList<Guid> DeadSince(DateTimeOffset cutoff) =>
        _runners.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList();

    /// <summary>True when at least one runner's last beat is at or after <paramref name="cutoff"/>.</summary>
    public bool AnyAliveSince(DateTimeOffset cutoff) => _runners.Any(kv => kv.Value.LastSeen >= cutoff);

    /// <summary>Drops a runner from tracking so its death triggers failover at most once.</summary>
    public void Forget(Guid serviceId) => _runners.TryRemove(serviceId, out _);

    /// <summary>True when this runner has been heard from at all (since this Core started).</summary>
    public bool IsKnown(Guid serviceId) => _runners.ContainsKey(serviceId);

    /// <summary>Everything currently tracked, newest beat first.</summary>
    public IReadOnlyList<RunnerLiveness> Snapshot() =>
        _runners.Values.OrderByDescending(r => r.LastSeen).ToList();
}
