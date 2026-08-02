using System.Security.Cryptography;
using Auxilia.PlatformData.Protection;

namespace Auxilia.PlatformData.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class AesGcmSettingsProtectorTests
{
    private static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    [Test]
    public void ProtectUnprotect_RoundTripsPlaintext()
    {
        var sut = new AesGcmSettingsProtector(Key());
        const string secret = """{"Token":"super-secret","Url":"https://example.com"}""";

        var protectedValue = sut.Protect(secret);

        Assert.That(protectedValue, Does.StartWith("enc1:"));
        Assert.That(protectedValue, Does.Not.Contain("super-secret"));
        Assert.That(sut.Unprotect(protectedValue), Is.EqualTo(secret));
    }

    [Test]
    public void Protect_SamePlaintextTwice_ProducesDifferentCiphertext()
    {
        var sut = new AesGcmSettingsProtector(Key());
        Assert.That(sut.Protect("x"), Is.Not.EqualTo(sut.Protect("x")), "Nonce must be random per call.");
    }

    [Test]
    public void Unprotect_WithWrongKey_Throws()
    {
        var protectedValue = new AesGcmSettingsProtector(Key()).Protect("secret");
        var other = new AesGcmSettingsProtector(Key());
        Assert.Throws<AuthenticationTagMismatchException>(() => other.Unprotect(protectedValue));
    }

    [Test]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        var sut = new AesGcmSettingsProtector(Key());
        var protectedValue = sut.Protect("secret");
        var blob = Convert.FromBase64String(protectedValue["enc1:".Length..]);
        blob[^1] ^= 0xFF;
        var tampered = "enc1:" + Convert.ToBase64String(blob);

        Assert.Throws<AuthenticationTagMismatchException>(() => sut.Unprotect(tampered));
    }

    [Test]
    public void Unprotect_ValueWithoutPrefix_Throws()
    {
        var sut = new AesGcmSettingsProtector(Key());
        Assert.Throws<CryptographicException>(() => sut.Unprotect("plain-value"));
    }
}
