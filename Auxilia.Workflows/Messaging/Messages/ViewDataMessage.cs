namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// One view item published by a workflow (ARCHITECTURE §15). <see cref="Sequence"/> is
/// per-(instance, view) monotonic so consumers can order and de-duplicate. Large blobs
/// belong in the Artifact Store, referenced from the payload — never inline.
/// </summary>
public sealed record ViewDataMessage(
    Guid WorkflowInstanceId,
    string ViewName,
    long Sequence,
    string PayloadJson)
{
    public const string ExchangeName = "workflow.view-data";
}
