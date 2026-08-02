namespace Auxilia.Workflows.Messaging.Messages;

public sealed record WorkflowStateMessage(
    Guid WorkflowInstanceId,
    WorkflowState State,
    string? ErrorMessage);
