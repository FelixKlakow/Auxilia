namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// One view item published by a workflow (ARCHITECTURE §15). <see cref="Sequence"/> counts
/// the instance's items across ALL its views (1, 2, 3, …) so consumers can order, de-duplicate,
/// spot a missing item, and resume after one. Large blobs belong in the Artifact Store,
/// referenced from the payload — never inline.
/// </summary>
public sealed record ViewDataMessage(
    Guid WorkflowInstanceId,
    string ViewName,
    long Sequence,
    string PayloadJson) : Auxilia.Messaging.IWorkloadScopedMessage
{
    // Topic exchange (selective routing) — a NEW name, because the retired fanout
    // "workflow.view-data" cannot be redeclared with a different type in place.
    public const string ExchangeName = "workflow.views";

    /// <summary>Routing key at publish and exact binding key: the instance id.</summary>
    public static string RoutingKeyFor(Guid instanceId) => instanceId.ToString();
}
