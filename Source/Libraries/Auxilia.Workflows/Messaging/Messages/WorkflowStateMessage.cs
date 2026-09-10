namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Terminal state report of a run. Authenticated like every other SDK-to-runner message: the
/// instance token proves the sender IS the instance and the workflow name binds it to the type
/// the token was issued for; the runner ignores reports it cannot verify.
/// </summary>
public sealed record WorkflowStateMessage(
    Guid WorkflowInstanceId,
    WorkflowState State,
    string? ErrorMessage,
    string? WorkflowName = null,
    string? InstanceToken = null);
