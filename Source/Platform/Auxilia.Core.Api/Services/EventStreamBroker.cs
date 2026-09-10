using System.Threading.Channels;
using Auxilia.Core.Contracts;

namespace Auxilia.Core.Api.Services;

/// <summary>
/// Receives event-audience transitions from the <see cref="EventStreamBroker"/> so bus
/// bindings can follow the audience (selective routing). A null <c>eventType</c> is an
/// unfiltered subscriber (match-all binding). Subscribe is awaited; a subscribe that throws
/// (incl. cancellation) must have rolled back its own bookkeeping, because it gets no
/// unsubscribe callback. Unsubscribe is best-effort and must not throw.
/// </summary>
public interface IEventStreamBindingListener
{
    Task EventInterestAddedAsync(string? eventType, CancellationToken ct);
    Task EventInterestRemovedAsync(string? eventType);
}

/// <summary>
/// In-memory registry of event SSE subscribers — the platform-event counterpart of
/// <see cref="ArtifactStreamBroker"/>: a subscriber declares the event type and/or work item
/// it cares about and receives only matching events, never the global feed. The bus fan-out
/// (<see cref="EventStreamPublisher"/>) calls <see cref="Publish"/>; each open
/// <c>GET /api/events/stream</c> holds a <see cref="Subscription"/> and drains its channel.
/// The type filter is also reported to the registered <see cref="IEventStreamBindingListener"/>
/// so this node ingests only event types someone is streaming (work-item filtering stays here —
/// it is not part of the routing key).
/// </summary>
public sealed class EventStreamBroker
{
    private readonly object _gate = new();
    private readonly List<Entry> _subscribers = new();
    private IEventStreamBindingListener? _listener;

    private sealed record Entry(string? EventType, string? WorkItemId, Channel<EventStreamEvent> Channel);

    /// <summary>Registers the (single) binding listener — the bus-side publisher.</summary>
    public void SetListener(IEventStreamBindingListener listener) => _listener = listener;

    /// <summary>
    /// Registers a subscriber (null filters match everything) and awaits the listener so the bus
    /// binding is in place when this returns; dispose to unregister.
    /// </summary>
    public async Task<Subscription> SubscribeAsync(string? eventType, string? workItemId, CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<EventStreamEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var entry = new Entry(
            string.IsNullOrWhiteSpace(eventType) ? null : eventType,
            string.IsNullOrWhiteSpace(workItemId) ? null : workItemId,
            channel);
        lock (_gate)
            _subscribers.Add(entry);

        var subscription = new Subscription(channel.Reader, () => Unsubscribe(entry));
        if (_listener is { } listener)
        {
            try
            {
                await listener.EventInterestAddedAsync(entry.EventType, ct);
            }
            catch
            {
                // A failed (or cancelled) subscribe has rolled back its own audience bookkeeping
                // — no unsubscribe callback, which would decrement a count it never incremented.
                Discard(entry);
                throw;
            }
        }
        return subscription;
    }

    /// <summary>Fans an event to every subscriber whose filters match. Never throws.</summary>
    public void Publish(EventStreamEvent evt)
    {
        List<Entry> snapshot;
        lock (_gate)
            snapshot = _subscribers.ToList();

        foreach (var entry in snapshot)
        {
            if (entry.EventType is { } type
                && !string.Equals(type, evt.Event.EventType, StringComparison.Ordinal))
                continue;
            if (entry.WorkItemId is { } workItem
                && !string.Equals(workItem, evt.Event.WorkItemId, StringComparison.Ordinal))
                continue;
            entry.Channel.Writer.TryWrite(evt);
        }
    }

    private void Unsubscribe(Entry entry)
    {
        if (!Discard(entry))
            return;
        // Best-effort unbind; listeners never throw here — a stale binding only over-delivers.
        _ = _listener?.EventInterestRemovedAsync(entry.EventType);
    }

    /// <summary>Removes the subscriber and completes its channel WITHOUT notifying the listener.</summary>
    private bool Discard(Entry entry)
    {
        lock (_gate)
        {
            if (!_subscribers.Remove(entry))
                return false;
        }
        entry.Channel.Writer.TryComplete();
        return true;
    }

    /// <summary>A single subscriber's read side; dispose to unregister and complete the channel.</summary>
    public sealed class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;

        internal Subscription(ChannelReader<EventStreamEvent> reader, Action unsubscribe)
        {
            Reader = reader;
            _unsubscribe = unsubscribe;
        }

        public ChannelReader<EventStreamEvent> Reader { get; }

        public void Dispose() => _unsubscribe();
    }
}
