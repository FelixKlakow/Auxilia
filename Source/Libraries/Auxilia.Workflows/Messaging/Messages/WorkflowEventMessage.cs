namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// A named platform event: published by a workflow container (via the SDK's event publisher)
/// or by the Core itself (run lifecycle). Events are small facts — large data belongs in the
/// Artifact Store, referenced from <see cref="PayloadJson"/>. Consumers de-duplicate by
/// <see cref="EventId"/>; the Core mirrors events into its store keyed by it.
/// </summary>
public sealed record WorkflowEventMessage(
    Guid EventId,
    string EventType,
    Guid WorkflowInstanceId,
    string WorkflowType,
    string WorkItemId,
    string? PayloadJson,
    DateTimeOffset TimestampUtc)
{
    // Topic exchange (selective routing), same scheme as workflow.artifacts.
    public const string ExchangeName = "workflow.events";

    /// <summary>
    /// Event types under this prefix are published by the platform itself (run lifecycle);
    /// the SDK refuses to publish them from a workflow.
    /// </summary>
    public const string ReservedPlatformPrefix = "run.";

    /// <summary>
    /// Routing key at publish and exact binding key: the event type, with AMQP topic
    /// wildcard characters neutralized so a type name can never widen a binding.
    /// </summary>
    public static string RoutingKeyFor(string eventType)
        => eventType.Replace('*', '-').Replace('#', '-');
}
