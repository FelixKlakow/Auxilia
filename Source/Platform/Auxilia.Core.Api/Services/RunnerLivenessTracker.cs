using System.Collections.Concurrent;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Tracks the last time each Core.Runner was heard from over the bus (via <c>RunnerHeartbeat</c>).
/// Pure in-memory liveness state — it never reads any database, so it respects the Core/Runner DB
/// isolation. A runner is "dead" once its last beat is older than the failover cutoff.
/// </summary>
public sealed class RunnerLivenessTracker
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastSeen = new();

    /// <summary>Records that <paramref name="serviceId"/> was seen at <paramref name="at"/> (keeps the latest).</summary>
    public void Record(Guid serviceId, DateTimeOffset at) =>
        _lastSeen.AddOrUpdate(serviceId, at, (_, existing) => at > existing ? at : existing);

    /// <summary>Service ids whose last beat is strictly older than <paramref name="cutoff"/>.</summary>
    public IReadOnlyList<Guid> DeadSince(DateTimeOffset cutoff) =>
        _lastSeen.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();

    /// <summary>True when at least one runner's last beat is at or after <paramref name="cutoff"/>.</summary>
    public bool AnyAliveSince(DateTimeOffset cutoff) => _lastSeen.Any(kv => kv.Value >= cutoff);

    /// <summary>Drops a runner from tracking so its death triggers failover at most once.</summary>
    public void Forget(Guid serviceId) => _lastSeen.TryRemove(serviceId, out _);

    /// <summary>True when this runner has been heard from at all (since this Core started).</summary>
    public bool IsKnown(Guid serviceId) => _lastSeen.ContainsKey(serviceId);
}
