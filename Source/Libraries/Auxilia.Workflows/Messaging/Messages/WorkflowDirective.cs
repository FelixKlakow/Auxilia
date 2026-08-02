namespace Auxilia.Workflows.Messaging.Messages;

public sealed record WorkflowDirective(
    Guid WorkflowInstanceId,
    WorkflowDirectiveKind Directive);
