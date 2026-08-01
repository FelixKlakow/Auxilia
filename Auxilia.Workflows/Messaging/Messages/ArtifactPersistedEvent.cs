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
    public const string ExchangeName = "workflow.artifact-events";
}
