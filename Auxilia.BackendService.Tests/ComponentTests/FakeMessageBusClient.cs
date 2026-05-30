using Auxilia.Messaging;

namespace Auxilia.BackendService.Tests.ComponentTests;

/// <summary>
///     In-memory fake message bus for component tests. Thread-safe.
///     Records all calls so tests can assert on them.
///     Delivers messages fed via <see cref="SimulateReceivedAsync{T}" /> to registered subscribers.
/// </summary>
public sealed class FakeMessageBusClient : IMessageBusClient
{
    private readonly List<string> _declaredQueues = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<(string Topic, object Message)> _publishedMessages = new();
    private readonly Dictionary<string, List<Func<object, CancellationToken, Task>>> _subscribers = new();

    public IReadOnlyList<string> DeclaredQueues => _declaredQueues;
    public IReadOnlyList<(string Topic, object Message)> PublishedMessages => _publishedMessages;

    public async Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _declaredQueues.Add(queueName);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _publishedMessages.Add((topic, message!));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        List<Func<object, CancellationToken, Task>> handlers;
        try
        {
            _publishedMessages.Add((exchangeName, message!));
            _subscribers.TryGetValue(exchangeName, out var list);
            handlers = list?.ToList() ?? new List<Func<object, CancellationToken, Task>>();
        }
        finally
        {
            _lock.Release();
        }

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
            {
                list = new List<Func<object, CancellationToken, Task>>();
                _subscribers[queueName] = list;
            }

            list.Add((msg, ct) => handler((T)msg, ct));
        }
        finally
        {
            _lock.Release();
        }

        return new NoOpDisposable();
    }

    public async Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
        string exchangeName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
        => await SubscribeAsync(exchangeName, handler, cancellationToken);

    /// <summary>Feeds a message as if it arrived from the broker.</summary>
    public async Task SimulateReceivedAsync<T>(string queueName, T message,
        CancellationToken cancellationToken = default)
    {
        List<Func<object, CancellationToken, Task>> handlers;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _subscribers.TryGetValue(queueName, out var list);
            handlers = list?.ToList() ?? new List<Func<object, CancellationToken, Task>>();
        }
        finally
        {
            _lock.Release();
        }

        foreach (var h in handlers)
            await h(message!, cancellationToken);
    }

    /// <summary>Waits until the predicate is satisfied or the timeout expires.</summary>
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
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}