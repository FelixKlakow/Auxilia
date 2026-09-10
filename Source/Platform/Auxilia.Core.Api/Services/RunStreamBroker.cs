using System.Threading.Channels;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Receives run-audience transitions from the <see cref="RunStreamBroker"/> so bus bindings can
/// follow the audience (selective routing). Subscribe is awaited — the binding exists before the
/// SSE response is flushed; a subscribe that throws (incl. cancellation) must have rolled back its
/// own bookkeeping, because it gets no unsubscribe callback. Unsubscribe is best-effort and must
/// not throw.
/// </summary>
public interface IRunStreamBindingListener
{
    Task RunSubscribedAsync(Guid runId, CancellationToken ct);
    Task RunUnsubscribedAsync(Guid runId);
}

/// <summary>
/// In-memory registry of live-view (SSE) subscribers keyed by run id. The bus fan-out
/// (<see cref="RunStreamPublisher"/>) calls <see cref="Publish"/>; each open
/// <c>GET /api/runs/{id}/stream</c> holds a <see cref="Subscription"/> and drains its channel. A frame
/// is delivered only to subscribers of its own <see cref="RunStreamEvent.RunId"/>. Every
/// subscribe/unsubscribe is reported to the registered <see cref="IRunStreamBindingListener"/> so
/// this node's bus bindings track exactly the runs with an open stream.
/// </summary>
public sealed class RunStreamBroker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, List<Channel<RunStreamEvent>>> _subscribers = new();
    private IRunStreamBindingListener? _listener;

    /// <summary>Registers the (single) binding listener — the bus-side publisher.</summary>
    public void SetListener(IRunStreamBindingListener listener) => _listener = listener;

    /// <summary>
    /// Registers a subscriber for <paramref name="runId"/> and awaits the listener so the bus
    /// binding is in place when this returns; dispose to unregister.
    /// </summary>
    public async Task<Subscription> SubscribeAsync(Guid runId, CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<RunStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(runId, out var list))
                _subscribers[runId] = list = new List<Channel<RunStreamEvent>>();
            list.Add(channel);
        }

        var subscription = new Subscription(this, runId, channel);
        if (_listener is { } listener)
        {
            try
            {
                await listener.RunSubscribedAsync(runId, ct);
            }
            catch
            {
                // A failed (or cancelled) subscribe has rolled back its own audience bookkeeping
                // — it gets NO unsubscribe callback, which would decrement a count it never
                // incremented and unbind a sibling stream's keys.
                Discard(runId, channel);
                throw;
            }
        }
        return subscription;
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
        if (!Discard(runId, channel))
            return;
        // Best-effort unbind; listeners never throw here — a stale binding only over-delivers.
        _ = _listener?.RunUnsubscribedAsync(runId);
    }

    /// <summary>Removes the subscriber and completes its channel WITHOUT notifying the listener.</summary>
    private bool Discard(Guid runId, Channel<RunStreamEvent> channel)
    {
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(runId, out var list))
                return false;
            if (!list.Remove(channel))
                return false;
            if (list.Count == 0)
                _subscribers.Remove(runId);
        }
        channel.Writer.TryComplete();
        return true;
    }

    /// <summary>A single subscriber's read side; dispose to unregister and complete the channel.</summary>
    public sealed class Subscription(RunStreamBroker broker, Guid runId, Channel<RunStreamEvent> channel) : IDisposable
    {
        public ChannelReader<RunStreamEvent> Reader => channel.Reader;

        public void Dispose() => broker.Unsubscribe(runId, channel);
    }
}
