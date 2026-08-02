namespace Auxilia.Messaging;

/// <summary>
///     A live topic-exchange subscription whose routing-key bindings can change while it is
///     consuming — the selective-routing primitive: a node binds only the keys it currently
///     has an audience for. Disposing cancels the subscription and drops its queue.
/// </summary>
public interface ITopicSubscription : IAsyncDisposable
{
    /// <summary>Adds a routing-key binding. Idempotent on the broker side.</summary>
    Task AddBindingAsync(string routingKey, CancellationToken cancellationToken = default);

    /// <summary>Removes a routing-key binding previously added.</summary>
    Task RemoveBindingAsync(string routingKey, CancellationToken cancellationToken = default);
}
