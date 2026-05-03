namespace Auxilia.Messaging;

/// <summary>
///     Abstraction over the message bus transport. Kept deliberately thin so implementations
///     can be swapped for fakes in tests without a real broker.
/// </summary>
public interface IMessageBusClient
{
    /// <summary>Declares a durable queue. Idempotent – safe to call on every startup.</summary>
    Task DeclareQueueAsync(string queueName, CancellationToken cancellationToken = default);

    /// <summary>Publishes a message to the given topic/queue name.</summary>
    Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Subscribes to a queue. The handler is invoked for every received message.
    ///     Returns an IAsyncDisposable that cancels the subscription when disposed.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync<T>(
        string queueName,
        Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken = default);
}