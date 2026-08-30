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

    /// <summary>
    ///     Declares a topic exchange (selective routing). Idempotent. The default degrades to the
    ///     fanout declaration — correct for in-memory fakes, which route by message type anyway;
    ///     broker implementations override with a real topic exchange.
    /// </summary>
    Task DeclareTopicExchangeAsync(string exchangeName, CancellationToken cancellationToken = default)
        => DeclareExchangeAsync(exchangeName, cancellationToken);

    /// <summary>
    ///     Publishes to a topic exchange under <paramref name="routingKey"/>; only queues whose
    ///     bindings match receive a copy. The default ignores the key and fans out — in-memory
    ///     fakes over-deliver, and consumers filter (routing is verified against a real broker).
    /// </summary>
    Task PublishToTopicExchangeAsync<T>(
        string exchangeName, string routingKey, T message, CancellationToken cancellationToken = default)
        => PublishToExchangeAsync(exchangeName, message, cancellationToken);

    /// <summary>
    ///     Creates a private auto-delete queue on a topic exchange, binds the given routing keys,
    ///     and subscribes. Bindings can be added/removed while consuming
    ///     (<see cref="ITopicSubscription"/>) — the per-node selective-ingest primitive for SSE
    ///     fan-out. The default degrades to an everything-subscription with no-op bindings.
    /// </summary>
    async Task<ITopicSubscription> SubscribeToTopicExchangeAsync<T>(
        string exchangeName,
        IReadOnlyCollection<string> routingKeys,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
        => new NonSelectiveTopicSubscription(
            await SubscribeToExchangeAsync(exchangeName, handler, cancellationToken));

    /// <summary>
    ///     Binds a durable NAMED queue to a topic exchange under <paramref name="bindingKey"/>
    ///     (mirrors bind <c>#</c>) and consumes it as competing consumers — the multi-node
    ///     bus→store mirror pattern. The default degrades to the shared fanout subscription.
    /// </summary>
    Task<IAsyncDisposable> SubscribeToTopicExchangeSharedAsync<T>(
        string exchangeName,
        string queueName,
        string bindingKey,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default)
        => SubscribeToExchangeSharedAsync(exchangeName, queueName, handler, cancellationToken);
}

/// <summary>
///     Wraps a plain exchange subscription as an <see cref="ITopicSubscription"/> whose binding
///     mutations are no-ops — the degraded mode of non-broker implementations, which deliver
///     everything and leave filtering to the consumer.
/// </summary>
public sealed class NonSelectiveTopicSubscription(IAsyncDisposable inner) : ITopicSubscription
{
    public Task AddBindingAsync(string routingKey, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RemoveBindingAsync(string routingKey, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}