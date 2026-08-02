namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Published when a run's declared output has been persisted to the artifact store.
/// Carries only the reference (ID + content hash) — payloads never travel the bus
/// (ARCHITECTURE §4). Enables the artifact-completion trigger (workflow chaining, §6).
/// </summary>
public sealed record ArtifactPersistedEvent(
    Guid ArtifactId,
    string ArtifactType,
    string WorkflowType,
    string WorkItemId,
    Guid RunInstanceId,
    int Version,
    string ContentHash,
    long SizeBytes,
    DateTimeOffset TimestampUtc)
{
    // Topic exchange (selective routing) — a NEW name, because the retired fanout
    // "workflow.artifact-events" cannot be redeclared with a different type in place.
    public const string ExchangeName = "workflow.artifacts";

    /// <summary>
    /// Routing key at publish and exact binding key: the artifact type, with AMQP topic
    /// wildcard characters neutralized so a type name can never widen a binding.
    /// </summary>
    public static string RoutingKeyFor(string artifactType)
        => artifactType.Replace('*', '-').Replace('#', '-');
}
