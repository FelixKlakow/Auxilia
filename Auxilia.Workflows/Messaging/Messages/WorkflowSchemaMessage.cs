namespace Auxilia.Workflows.Messaging.Messages;

[Obsolete("Schema is now pre-loaded from the ZIP package. WorkflowSchemaMessage will be removed in a future release.")]
public sealed record WorkflowSchemaMessage(
    Guid WorkflowInstanceId,
    WorkflowSchema Schema);
