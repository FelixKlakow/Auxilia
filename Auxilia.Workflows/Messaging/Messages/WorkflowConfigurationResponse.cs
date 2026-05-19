using System.Text.Json.Serialization;

namespace Auxilia.Workflows.Messaging.Messages;

public record WorkflowConfigurationResponse(
    Guid WorkflowInstanceId,
    bool Success,
    string? ErrorMessage,
    IReadOnlyDictionary<string, EncryptedSlotConfiguration> Slots,
    IReadOnlyDictionary<string, ISignalHandlerDescriptor> SignalHandlers)
{
    [JsonConstructor]
    public WorkflowConfigurationResponse(
        Guid workflowInstanceId,
        bool success,
        string? errorMessage,
        IReadOnlyDictionary<string, EncryptedSlotConfiguration> slots)
        : this(workflowInstanceId, success, errorMessage, slots,
               new Dictionary<string, ISignalHandlerDescriptor>())
    {
    }
}
