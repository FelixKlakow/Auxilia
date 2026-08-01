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

    /// <summary>
    ///     Binds a durable NAMED queue to the fanout exchange and consumes it — competing
    ///     consumers: across all subscribers sharing <paramref name="queueName"/> each message is
    ///     processed once (at-least-once bus semantics apply), instead of once per subscriber.
    ///     Use for bus→store mirrors so N service nodes don't multiply writes; per-node fan-outs
    ///     (SSE brokers) keep <see cref="SubscribeToExchangeAsync{T}"/>. The default
    ///     implementation degrades to a per-subscriber copy — identical semantics on a single
    ///     node and for in-memory fakes; broker implementations override with a real shared queue.
    /// </summary>
    Task<IAsyncDisposable> SubscribeToExchangeSharedAsync<T>(
        string exchangeName,
        string queueName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
        => SubscribeToExchangeAsync(exchangeName, handler, cancellationToken);
}