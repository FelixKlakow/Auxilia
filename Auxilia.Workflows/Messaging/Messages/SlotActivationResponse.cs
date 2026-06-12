namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Delivered to the instance's exclusive response queue. The credential expires at
/// <see cref="ExpiresUtc"/>; the SDK transparently re-requests after expiry, so
/// long-living instances never hold indefinitely valid secrets.
/// </summary>
public sealed record SlotActivationResponse(
    Guid WorkflowInstanceId,
    string SlotName,
    bool Success,
    string? ErrorMessage,
    EncryptedSlotConfiguration? Slot,
    DateTimeOffset? ExpiresUtc = null);
