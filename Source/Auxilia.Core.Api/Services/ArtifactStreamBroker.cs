using System.Threading.Channels;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// In-memory registry of artifact SSE subscribers — the artifact counterpart of
/// <see cref="RunStreamBroker"/>, but FILTERED: a subscriber declares the artifact type and/or
/// work item it cares about and receives only matching events, never the global feed
/// (backlog "Core scalability": no per-client firehose). The bus fan-out
/// (<see cref="ArtifactStreamPublisher"/>) calls <see cref="Publish"/>; each open
/// <c>GET /api/artifacts/stream</c> holds a <see cref="Subscription"/> and drains its channel.
/// </summary>
public sealed class ArtifactStreamBroker
{
    private readonly object _gate = new();
    private readonly List<Entry> _subscribers = new();

    private sealed record Entry(string? ArtifactType, string? WorkItemId, Channel<ArtifactStreamEvent> Channel);

    /// <summary>Registers a subscriber; null filters match everything. Dispose to unregister.</summary>
    public Subscription Subscribe(string? artifactType, string? workItemId)
    {
        var channel = Channel.CreateUnbounded<ArtifactStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var entry = new Entry(
            string.IsNullOrWhiteSpace(artifactType) ? null : artifactType,
            string.IsNullOrWhiteSpace(workItemId) ? null : workItemId,
            channel);
        lock (_gate)
            _subscribers.Add(entry);
        return new Subscription(channel.Reader, () => Unsubscribe(entry));
    }

    /// <summary>Fans an event to every subscriber whose filters match. Never throws.</summary>
    public void Publish(ArtifactStreamEvent evt)
    {
        List<Entry> snapshot;
        lock (_gate)
            snapshot = _subscribers.ToList();

        foreach (var entry in snapshot)
        {
            if (entry.ArtifactType is { } type
                && !string.Equals(type, evt.Artifact.ArtifactType, StringComparison.Ordinal))
                continue;
            if (entry.WorkItemId is { } workItem
                && !string.Equals(workItem, evt.Artifact.WorkItemId, StringComparison.Ordinal))
                continue;
            entry.Channel.Writer.TryWrite(evt);
        }
    }

    private void Unsubscribe(Entry entry)
    {
        lock (_gate)
            _subscribers.Remove(entry);
        entry.Channel.Writer.TryComplete();
    }

    /// <summary>A single subscriber's read side; dispose to unregister and complete the channel.</summary>
    public sealed class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;

        internal Subscription(ChannelReader<ArtifactStreamEvent> reader, Action unsubscribe)
        {
            Reader = reader;
            _unsubscribe = unsubscribe;
        }

        public ChannelReader<ArtifactStreamEvent> Reader { get; }

        public void Dispose() => _unsubscribe();
    }
}
