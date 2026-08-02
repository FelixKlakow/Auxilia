using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Messaging.Messages;

namespace Auxilia.Workflows.Tests.Crypto;

[TestFixture]
[Category("Unit")]
public class SlotConfigurationCryptoTests
{
    [Test]
    public void Decrypt_ValidEncryptedSlotConfiguration_ReturnsCorrectSlotConfiguration()
    {
        using var keyPair = new EphemeralKeyPair();

        var settings = new Dictionary<string, string> { ["key"] = "value" };
        var json = JsonSerializer.Serialize(settings);

        var publicKeyBytes = Convert.FromBase64String(keyPair.PublicKeyBase64);
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

        var encryptedBytes = rsa.Encrypt(Encoding.UTF8.GetBytes(json), RSAEncryptionPadding.OaepSHA256);
        var encrypted = new EncryptedSlotConfiguration("test", Convert.ToBase64String(encryptedBytes));

        var result = SlotConfigurationCrypto.Decrypt(encrypted, keyPair);

        Assert.That(result.ProviderType, Is.EqualTo("test"));
        Assert.That(result.Settings["key"], Is.EqualTo("value"));
    }
}
