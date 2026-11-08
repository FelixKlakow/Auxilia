namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Delivered to the instance's exclusive response queue. The delivered configuration is valid
/// until <see cref="ExpiresUtc"/>; a handler that needs it longer fetches the slot again through
/// <see cref="ISlotConfigurationSource"/> and receives what the connector holds then.
/// </summary>
public sealed record SlotActivationResponse(
    Guid WorkflowInstanceId,
    string SlotName,
    bool Success,
    string? ErrorMessage,
    EncryptedSlotConfiguration? Slot,
    DateTimeOffset? ExpiresUtc = null) : Auxilia.Messaging.IPlatformOnlyMessage;
