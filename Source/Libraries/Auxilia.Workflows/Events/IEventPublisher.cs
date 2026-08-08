namespace Auxilia.Workflows.Events;

/// <summary>Publishes one of the workflow's declared events to the platform.</summary>
public interface IEventPublisher
{
    /// <summary>Serialises <paramref name="payload"/> and publishes the named event.</summary>
    Task PublishAsync<TPayload>(
        string eventType, TPayload payload, string? workItemId = null, CancellationToken ct = default);

    /// <summary>Publishes the named event without a payload.</summary>
    Task PublishAsync(string eventType, string? workItemId = null, CancellationToken ct = default);
}
