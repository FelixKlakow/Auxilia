namespace Auxilia.Workflows.Messaging.Messages;

public sealed record RegisterSlotProviderCommand(
    string ProviderType,
    string DllPath,
    IReadOnlyList<SettingDescriptor>? Settings = null,
    IReadOnlyList<string>? Contracts = null,
    string? Category = null);
