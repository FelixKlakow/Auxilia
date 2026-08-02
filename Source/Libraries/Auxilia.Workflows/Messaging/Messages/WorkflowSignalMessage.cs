namespace Auxilia.Workflows.Messaging.Messages;

public sealed record WorkflowSignalMessage(
    Guid WorkflowInstanceId,
    string SignalName,
    string PayloadJson);
