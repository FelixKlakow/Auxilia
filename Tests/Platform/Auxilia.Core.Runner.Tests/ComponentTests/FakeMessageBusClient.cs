using Auxilia.Messaging;

namespace Auxilia.Core.Runner.Tests.ComponentTests;

/// <summary>
/// In-memory <see cref="IMessageBusClient"/> for component tests.
/// Thread-safe. Records all published messages and delivers messages fed via
/// <see cref="SimulateReceivedAsync{T}"/> to all registered subscribers on that queue.
/// </summary>
public sealed class FakeMessageBusClient : IMessageBusClient
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<string> _declaredQueues = [];
    private readonly List<(string Topic, object Message)> _publishedMessages = [];
    private readonly Dictionary<string, List<Func<object, CancellationToken, Task>>> _subscribers = new();

    public IReadOnlyList<string> DeclaredQueues => _declaredQueues;
    public IReadOnlyList<(string Topic, object Message)> PublishedMessages => _publishedMessages;

    public async Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try { _declaredQueues.Add(queueName); }
        finally { _lock.Release(); }
    }

    public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task PublishAsync<T>(
        string topic, T message, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try { _publishedMessages.Add((topic, message!)); }
        finally { _lock.Release(); }
    }

    public async Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        List<Func<object, CancellationToken, Task>> handlers;
        try
        {
            _publishedMessages.Add((exchangeName, message!));
            _subscribers.TryGetValue(exchangeName, out var list);
            handlers = list?.ToList() ?? [];
        }
        finally { _lock.Release(); }

        foreach (var h in handlers)
            await h(message!, cancellationToken);
    }

    public async Task<IAsyncDisposable> SubscribeAsync<T>(
        string queueName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!_subscribers.TryGetValue(queueName, out var list))
                _subscribers[queueName] = list = [];
            list.Add((msg, ct) => msg is T typedMsg ? handler(typedMsg, ct) : Task.CompletedTask);
        }
        finally { _lock.Release(); }

        return new NoOpDisposable();
    }

    public async Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
        string exchangeName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
        => await SubscribeAsync(exchangeName, handler, cancellationToken);

    /// <summary>Feeds a message as if it arrived from the broker on <paramref name="queueName"/>.</summary>
    public async Task SimulateReceivedAsync<T>(
        string queueName, T message, CancellationToken cancellationToken = default)
    {
        List<Func<object, CancellationToken, Task>> handlers;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _subscribers.TryGetValue(queueName, out var list);
            handlers = list?.ToList() ?? [];
        }
        finally { _lock.Release(); }

        foreach (var h in handlers)
            await h(message!, cancellationToken);
    }

    /// <summary>
    /// Polls until <paramref name="predicate"/> returns <c>true</c> or <paramref name="timeout"/> elapses.
    /// Returns the final predicate value.
    /// </summary>
    public async Task<bool> WaitForConditionAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(50);
        }
        return predicate();
    }

    private sealed class NoOpDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

