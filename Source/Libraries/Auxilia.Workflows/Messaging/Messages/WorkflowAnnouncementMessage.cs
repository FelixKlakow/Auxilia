namespace Auxilia.Workflows.Messaging.Messages;

public sealed record WorkflowAnnouncementMessage(
    Guid WorkflowInstanceId,
    string WorkflowName,
    string PublicKey,
    string ResponseTopic,
    string? InstanceToken = null);
