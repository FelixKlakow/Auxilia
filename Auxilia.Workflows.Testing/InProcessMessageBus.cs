using System.Collections.Concurrent;
using Auxilia.Messaging;

namespace Auxilia.Workflows.Testing;

public sealed class InProcessMessageBus : IMessageBusClient
{
    private readonly ConcurrentDictionary<string, List<Subscription>> _subscriptions = new();
    private readonly Lock _lock = new();

    public Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default)
    {
        List<Subscription> snapshot;
        lock (_lock)
        {
            if (!_subscriptions.TryGetValue(topic, out var list))
                return;
            snapshot = [.. list];
        }

        foreach (var subscription in snapshot)
        {
            if (subscription.Handler is Func<T, CancellationToken, Task> typed)
                await typed(message, cancellationToken);
        }
    }

    public Task<IAsyncDisposable> SubscribeAsync<T>(
        string queueName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
    {
        var subscription = new Subscription(handler);

        lock (_lock)
        {
            var list = _subscriptions.GetOrAdd(queueName, _ => []);
            list.Add(subscription);
        }

        IAsyncDisposable disposable = new SubscriptionHandle(() =>
        {
            lock (_lock)
            {
                if (_subscriptions.TryGetValue(queueName, out var list))
                    list.Remove(subscription);
            }
        });

        return Task.FromResult(disposable);
    }

    private sealed class Subscription(Delegate handler)
    {
        public Delegate Handler { get; } = handler;
    }

    private sealed class SubscriptionHandle(Action onDispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}
