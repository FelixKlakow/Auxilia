using System.Text.Json;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Crypto;

internal record SlotConfigurationDto(
    string ProviderType,
    IReadOnlyDictionary<string, string> Settings);

internal static class SlotConfigurationCrypto
{
    internal static SlotConfiguration Decrypt(EncryptedSlotConfiguration encrypted, EphemeralKeyPair keyPair)
    {
        var plaintext = keyPair.Decrypt(encrypted.EncryptedSettings);
        var dto = JsonSerializer.Deserialize<SlotConfigurationDto>(plaintext)
            ?? throw new InvalidOperationException("Failed to deserialise slot configuration.");
        return new SlotConfiguration(dto.ProviderType, dto.Settings);
    }
}
