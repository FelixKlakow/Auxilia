using System.Security.Cryptography;
using Auxilia.Workflows.Packer;

namespace Auxilia.Workflows.Packer.Tests;

[TestFixture]
public sealed class RsaFileSigningProviderTests : IDisposable
{
    private RSA _rsa = null!;
    private string _pemFilePath = null!;
    private RsaFileSigningProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _rsa = RSA.Create(4096);
        _pemFilePath = Path.GetTempFileName();
        File.WriteAllText(_pemFilePath, _rsa.ExportRSAPrivateKeyPem());
        _provider = new RsaFileSigningProvider(_pemFilePath);
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _rsa.Dispose();
        if (File.Exists(_pemFilePath))
            File.Delete(_pemFilePath);
    }

    [Test]
    public void Sign_ProducesVerifiableSignature()
    {
        var data = "hello world"u8.ToArray();
        var sig = _provider.Sign(data);

        var hash = SHA256.HashData(data);
        Assert.That(_rsa.VerifyHash(hash, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), Is.True);
    }

    [Test]
    public void PublicKeyBase64_MatchesExportedKey()
    {
        var expected = _rsa.ExportSubjectPublicKeyInfo();
        var actual = Convert.FromBase64String(_provider.PublicKeyBase64);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Sign_WithDifferentKey_FailsVerification()
    {
        var data = "test data"u8.ToArray();
        var sig = _provider.Sign(data);

        using var otherRsa = RSA.Create(4096);
        var hash = SHA256.HashData(data);
        Assert.That(otherRsa.VerifyHash(hash, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pss), Is.False);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _rsa.Dispose();
    }
}
