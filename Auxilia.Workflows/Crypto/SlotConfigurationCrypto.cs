using System.Text.Json;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Crypto;

internal static class SlotConfigurationCrypto
{
    internal static SlotConfiguration Decrypt(EncryptedSlotConfiguration encrypted, EphemeralKeyPair keyPair)
    {
        var plaintext = keyPair.Decrypt(encrypted.EncryptedSettings);
        // EncryptedSettings contains only the settings dictionary (provider-type is stored
        // unencrypted in EncryptedSlotConfiguration.ProviderType).
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)
                       ?? new Dictionary<string, string>();
        return new SlotConfiguration(encrypted.ProviderType, settings);
    }
}
