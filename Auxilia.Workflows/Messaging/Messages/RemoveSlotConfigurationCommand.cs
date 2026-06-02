namespace Auxilia.Workflows.Messaging.Messages;

public sealed record RemoveSlotConfigurationCommand(
    string WorkflowType,
    string SlotName);
