namespace Auxilia.Workflows.Messaging.Messages;

public sealed record EncryptedSlotConfiguration(
    string ProviderType,
    string EncryptedSettings);
