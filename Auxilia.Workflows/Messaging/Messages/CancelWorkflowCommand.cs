namespace Auxilia.Workflows.Messaging.Messages;

public sealed record CancelWorkflowCommand(
    /// <summary>The instance ID of the running workflow to cancel.</summary>
    Guid WorkflowInstanceId);
