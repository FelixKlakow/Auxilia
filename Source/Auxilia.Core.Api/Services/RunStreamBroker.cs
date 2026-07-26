using System.Threading.Channels;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// In-memory registry of live-view (SSE) subscribers keyed by run id — the Core.Api analogue of the
/// BackendService <c>LiveViewBroker</c>, rebuilt for the pull-based SSE endpoint. The bus fan-out
/// (<see cref="RunStreamPublisher"/>) calls <see cref="Publish"/>; each open
/// <c>GET /api/runs/{id}/stream</c> holds a <see cref="Subscription"/> and drains its channel. A frame
/// is delivered only to subscribers of its own <see cref="RunStreamEvent.RunId"/>.
/// </summary>
public sealed class RunStreamBroker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<Channel<RunStreamEvent>>> _subscribers = new();

    /// <summary>Registers a subscriber for <paramref name="runId"/>; dispose to unregister.</summary>
    public Subscription Subscribe(Guid runId)
    {
        var channel = Channel.CreateUnbounded<RunStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(runId, out var list))
                _subscribers[runId] = list = new List<Channel<RunStreamEvent>>();
            list.Add(channel);
        }
        return new Subscription(this, runId, channel);
    }

    /// <summary>Fans an event to every subscriber of its run id. Never throws.</summary>
    public void Publish(RunStreamEvent evt)
    {
        List<Channel<RunStreamEvent>>? snapshot;
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(evt.RunId, out var list))
                return;
            snapshot = list.ToList();
        }
        foreach (var channel in snapshot)
            channel.Writer.TryWrite(evt);
    }

    private void Unsubscribe(Guid runId, Channel<RunStreamEvent> channel)
    {
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(runId, out var list))
                return;
            list.Remove(channel);
            if (list.Count == 0)
                _subscribers.Remove(runId);
        }
        channel.Writer.TryComplete();
    }

    /// <summary>A single subscriber's read side; dispose to unregister and complete the channel.</summary>
    public sealed class Subscription(RunStreamBroker broker, Guid runId, Channel<RunStreamEvent> channel) : IDisposable
    {
        public ChannelReader<RunStreamEvent> Reader => channel.Reader;

        public void Dispose() => broker.Unsubscribe(runId, channel);
    }
}
