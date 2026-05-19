using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.SteeringInstance.Workflows;

public readonly record struct ResolverResult(
    bool IsSuccess,
    IReadOnlyDictionary<string, EncryptedSlotConfiguration> Slots,
    IReadOnlyDictionary<string, ISignalHandlerDescriptor> SignalHandlers,
    string? FailureReason)
{
    public static ResolverResult Ok(
        IReadOnlyDictionary<string, EncryptedSlotConfiguration> slots,
        IReadOnlyDictionary<string, ISignalHandlerDescriptor> signalHandlers)
        => new(true, slots, signalHandlers, null);

    public static ResolverResult Fail(string reason)
        => new(false,
               new Dictionary<string, EncryptedSlotConfiguration>(),
               new Dictionary<string, ISignalHandlerDescriptor>(),
               reason);
}
