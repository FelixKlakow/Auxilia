using Auxilia.Messaging;

namespace Auxilia.WorkflowStudio.Tests;

/// <summary>
/// In-memory fake message bus for Studio tests. Records published messages and delivers exchange
/// messages fed via <see cref="SimulateReceivedAsync{T}" /> (or published) to registered subscribers,
/// so the artifact-completion trigger can be driven without a broker.
/// </summary>
public sealed class FakeMessageBusClient : IMessageBusClient
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<(string Topic, object Message)> _publishedMessages = new();
    private readonly Dictionary<string, List<Func<object, CancellationToken, Task>>> _subscribers = new();

    public IReadOnlyList<(string Topic, object Message)> PublishedMessages
    {
        get { lock (_publishedMessages) return _publishedMessages.ToList(); }
    }

    public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        lock (_publishedMessages) _publishedMessages.Add((topic, message!));
        return Task.CompletedTask;
    }

    public async Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
    {
        lock (_publishedMessages) _publishedMessages.Add((exchangeName, message!));
        await SimulateReceivedAsync(exchangeName, message, cancellationToken);
    }

    public async Task<IAsyncDisposable> SubscribeAsync<T>(
        string queueName, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_subscribers.TryGetValue(queueName, out var list))
                _subscribers[queueName] = list = new List<Func<object, CancellationToken, Task>>();
            list.Add((msg, ct) => handler((T)msg, ct));
        }
        finally { _lock.Release(); }
        return new NoOpDisposable();
    }

    public Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
        string exchangeName, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken = default)
        => SubscribeAsync(exchangeName, handler, cancellationToken);

    /// <summary>Feeds a message as if it arrived from the broker.</summary>
    public async Task SimulateReceivedAsync<T>(string queueOrExchange, T message, CancellationToken cancellationToken = default)
    {
        List<Func<object, CancellationToken, Task>> handlers;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _subscribers.TryGetValue(queueOrExchange, out var list);
            handlers = list?.ToList() ?? new List<Func<object, CancellationToken, Task>>();
        }
        finally { _lock.Release(); }

        foreach (var handler in handlers)
            await handler(message!, cancellationToken);
    }

    private sealed class NoOpDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
