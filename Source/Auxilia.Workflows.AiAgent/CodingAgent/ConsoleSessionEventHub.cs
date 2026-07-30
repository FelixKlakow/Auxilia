namespace Auxilia.Workflows.AiAgent.CodingAgent;

/// <summary>
/// Fans one provider event source out to several consumers. Every console CLI in the
/// container posts to the SAME provider listener, so a run with more than one driven console
/// (per-stage agents) shares one source: each console subscribes through
/// <see cref="CreateSource"/>, run-level view routing registers once via <see cref="OnEvent"/>.
/// Broadcasting is safe because the pipeline drives one console at a time — only the driving
/// console holds an open turn waiter.
/// </summary>
public sealed class ConsoleSessionEventHub(IConsoleSessionEventSource inner) : IAsyncDisposable
{
    private readonly List<Func<ConsoleSessionEvent, CancellationToken, Task>> _handlers = [];
    private readonly Lock _gate = new();
    private bool _started;

    /// <summary>A permanent run-level consumer (view routing) — not tied to any console.</summary>
    public void OnEvent(Func<ConsoleSessionEvent, CancellationToken, Task> handler)
    {
        lock (_gate)
        {
            _handlers.Add(handler);
        }
    }

    /// <summary>A per-console facade; its Start/Stop only manage the console's subscription.</summary>
    public IConsoleSessionEventSource CreateSource() => new Subscription(this);

    private async Task StartInnerAsync(CancellationToken cancellationToken)
    {
        bool start;
        lock (_gate)
        {
            start = !_started;
            _started = true;
        }
        if (start)
            await inner.StartAsync(DispatchAsync, cancellationToken);
    }

    private async Task DispatchAsync(ConsoleSessionEvent evt, CancellationToken ct)
    {
        Func<ConsoleSessionEvent, CancellationToken, Task>[] handlers;
        lock (_gate)
        {
            handlers = [.. _handlers];
        }
        foreach (var handler in handlers)
            await handler(evt, ct);
    }

    public async ValueTask DisposeAsync()
    {
        bool stop;
        lock (_gate)
        {
            stop = _started;
            _started = false;
            _handlers.Clear();
        }
        if (stop)
            await inner.StopAsync();
    }

    private sealed class Subscription(ConsoleSessionEventHub hub) : IConsoleSessionEventSource
    {
        private Func<ConsoleSessionEvent, CancellationToken, Task>? _handler;

        public async Task StartAsync(
            Func<ConsoleSessionEvent, CancellationToken, Task> onEvent, CancellationToken cancellationToken)
        {
            _handler = onEvent;
            lock (hub._gate)
            {
                hub._handlers.Add(onEvent);
            }
            await hub.StartInnerAsync(cancellationToken);
        }

        public ValueTask StopAsync()
        {
            if (_handler is { } handler)
                lock (hub._gate)
                {
                    hub._handlers.Remove(handler);
                }
            _handler = null;
            // The inner source stops with the hub — other consoles may still be listening.
            return ValueTask.CompletedTask;
        }
    }
}
