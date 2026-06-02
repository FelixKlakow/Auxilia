namespace Auxilia.Workflows.Messaging.Messages;

public sealed record RegisterSlotProviderCommand(
    string ProviderType,
    string DllPath);
