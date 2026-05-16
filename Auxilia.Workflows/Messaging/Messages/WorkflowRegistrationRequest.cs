namespace Auxilia.Workflows.Messaging.Messages;

public record WorkflowRegistrationRequest(
    Guid WorkflowInstanceId,
    WorkflowManifest Manifest,
    string PublicKey,
    string ResponseTopic);
