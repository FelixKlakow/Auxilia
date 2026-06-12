namespace Auxilia.Workflows.Messaging.Messages;

/// <summary>
/// Sent by the SDK when the workflow first uses a slot (or its credential expired):
/// credentials are delivered just-in-time per slot, never as an upfront bundle
/// (ARCHITECTURE §4/§7). Authenticated with the instance token issued at launch.
/// </summary>
public sealed record SlotActivationRequest(
    Guid WorkflowInstanceId,
    string SlotName,
    /// <summary>Base64 DER ephemeral public key — the response is encrypted for it.</summary>
    string PublicKey,
    string? InstanceToken = null);
