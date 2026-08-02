namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// An opaque input delivered into a <em>running</em> workflow instance (ARCHITECTURE steering
/// Flow 2/3): the Core authorizes and audits the caller, then publishes this to the instance's
/// pre-created response queue. The platform never interprets <see cref="PayloadJson"/> — decision
/// semantics live in the workflow (and its counterpart client codec).
/// </summary>
public sealed record WorkflowInputMessage(
    Guid WorkflowInstanceId,
    string PayloadJson,
    DateTimeOffset TimestampUtc);
