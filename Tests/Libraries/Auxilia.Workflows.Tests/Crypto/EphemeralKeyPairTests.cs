using System.Security.Cryptography;
using System.Text;
using Auxilia.Workflows.Crypto;

namespace Auxilia.Workflows.Tests.Crypto;

[TestFixture]
[Category("Unit")]
public class EphemeralKeyPairTests
{
    [Test]
    public void RoundTrip_EncryptThenDecrypt_ReturnsOriginalPlaintext()
    {
        const string plaintext = "hello-world";

        using var keyPair = new EphemeralKeyPair();
        var publicKeyBytes = Convert.FromBase64String(keyPair.PublicKeyBase64);

        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKeyBytes, out _);

        var encrypted = Convert.ToBase64String(
            rsa.Encrypt(Encoding.UTF8.GetBytes(plaintext), RSAEncryptionPadding.OaepSHA256));

        var result = keyPair.Decrypt(encrypted);

        Assert.That(result, Is.EqualTo(plaintext));
    }

    [Test]
    public void TwoInstances_ProduceDifferentPublicKeys()
    {
        using var kp1 = new EphemeralKeyPair();
        using var kp2 = new EphemeralKeyPair();

        Assert.That(kp1.PublicKeyBase64, Is.Not.EqualTo(kp2.PublicKeyBase64));
    }

    [Test]
    public void Dispose_DoesNotThrow()
    {
        var kp = new EphemeralKeyPair();
        Assert.DoesNotThrow(() => kp.Dispose());
    }
}
