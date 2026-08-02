namespace Auxilia.Workflows.Messaging.Messages;

public sealed record WorkflowNotificationMessage(
    Guid WorkflowInstanceId,
    string SignalName,
    string PayloadJson);
