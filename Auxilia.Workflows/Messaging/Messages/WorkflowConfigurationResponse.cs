namespace Auxilia.Workflows.Messaging.Messages;

public record WorkflowConfigurationResponse(
    Guid WorkflowInstanceId,
    bool Success,
    string? ErrorMessage,
    IReadOnlyDictionary<string, EncryptedSlotConfiguration> Slots);
