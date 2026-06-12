using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.BackendService.Dashboard;

/// <summary>
/// In-process live channel between the bus fan-out handlers and Blazor Server circuits
/// (backplane-less v1): pages on this host subscribe here instead of opening a SignalR
/// client connection back into the same process. The SignalR hub remains the path for
/// external clients; the pages' store poll remains as the degraded-mode fallback.
/// </summary>
public sealed class LiveViewBroker
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Subscriber> _subscribers = new();

    private sealed record Subscriber(Action<ViewDataMessage>? OnViewData, Action<WorkflowStatusEvent>? OnStatus);

    public IDisposable Subscribe(
        Action<ViewDataMessage>? onViewData = null, Action<WorkflowStatusEvent>? onStatus = null)
    {
        var id = Guid.NewGuid();
        lock (_gate)
            _subscribers[id] = new Subscriber(onViewData, onStatus);
        return new Subscription(this, id);
    }

    public void Publish(ViewDataMessage message)
        => Dispatch(s => s.OnViewData?.Invoke(message));

    public void Publish(WorkflowStatusEvent statusEvent)
        => Dispatch(s => s.OnStatus?.Invoke(statusEvent));

    private void Dispatch(Action<Subscriber> deliver)
    {
        List<Subscriber> snapshot;
        lock (_gate)
            snapshot = _subscribers.Values.ToList();

        foreach (var subscriber in snapshot)
        {
            try
            {
                deliver(subscriber);
            }
            catch
            {
                // A faulted circuit must never break fan-out to the remaining subscribers.
            }
        }
    }

    private sealed class Subscription(LiveViewBroker broker, Guid id) : IDisposable
    {
        public void Dispose()
        {
            lock (broker._gate)
                broker._subscribers.Remove(id);
        }
    }
}
