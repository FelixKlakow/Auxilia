using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SteeringInstance.Workflows;

public readonly record struct ResolverResult(
    bool IsSuccess,
    IReadOnlyDictionary<string, EncryptedSlotConfiguration> Slots,
    string? FailureReason)
{
    public static ResolverResult Ok(IReadOnlyDictionary<string, EncryptedSlotConfiguration> slots)
        => new(true, slots, null);

    public static ResolverResult Fail(string reason)
        => new(false, new Dictionary<string, EncryptedSlotConfiguration>(), reason);
}
