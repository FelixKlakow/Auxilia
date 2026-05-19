namespace Auxilia.Workflows.Messaging.Messages;

public sealed record WorkflowSignalRouteMessage(
    string TargetWorkflowName,
    string OriginSignalName,
    string PayloadJson);
