namespace Auxilia.Messaging;

/// <summary>
///     Abstraction over the message bus transport. Kept deliberately thin so implementations
///     can be swapped for fakes in tests without a real broker.
/// </summary>
public interface IMessageBusClient
{
    /// <summary>Declares a durable queue. Idempotent – safe to call on every startup.</summary>
    Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default);

    /// <summary>Declares a fanout exchange. Idempotent – safe to call on every startup.</summary>
    Task DeclareExchangeAsync(string exchangeName, CancellationToken cancellationToken = default);

    /// <summary>Publishes a message to the given topic/queue name.</summary>
    Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default);

    /// <summary>Publishes a message to a fanout exchange so every bound queue receives a copy.</summary>
    Task PublishToExchangeAsync<T>(string exchangeName, T message, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Subscribes to a queue. The handler is invoked for every received message.
    ///     Returns an IAsyncDisposable that cancels the subscription when disposed.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync<T>(
        string queueName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Creates a private exclusive queue, binds it to the fanout exchange, and subscribes.
    ///     Each caller gets its own copy of every message published to the exchange.
    ///     Returns an IAsyncDisposable that cancels the subscription and removes the queue when disposed.
    /// </summary>
    Task<IAsyncDisposable> SubscribeToExchangeAsync<T>(
        string exchangeName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);
}