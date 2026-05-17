namespace Auxilia.Workflows.Messaging.Messages;

public sealed record WorkflowSchemaMessage(
    Guid WorkflowInstanceId,
    WorkflowSchema Schema);
