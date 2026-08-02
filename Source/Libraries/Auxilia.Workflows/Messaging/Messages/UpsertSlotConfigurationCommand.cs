namespace Auxilia.Workflows.Messaging.Messages;

public sealed record UpsertSlotConfigurationCommand(
    string WorkflowType,
    string SlotName,
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings);
