using System.Security.Cryptography;
using Auxilia.Workflows.Crypto;
using Microsoft.Extensions.Logging;
using Moq;

namespace Auxilia.Workflows.Tests.Crypto;

[TestFixture]
[Category("Unit")]
public class PluginManifestVerifierTests
{
    private RSA _rsa = null!;
    private byte[] _assemblyBytes = null!;
    private PluginManifest _validManifest = null!;

    [SetUp]
    public void SetUp()
    {
        _rsa = RSA.Create(2048);
        _assemblyBytes = "fake assembly content"u8.ToArray();

        var hash = SHA256.HashData(_assemblyBytes);
        var signature = _rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var publicKey = _rsa.ExportSubjectPublicKeyInfo();

        _validManifest = new PluginManifest(
            ProviderType: "test-provider",
            ContentHashBase64: Convert.ToBase64String(hash),
            SignatureBase64: Convert.ToBase64String(signature),
            PublicKeyBase64: Convert.ToBase64String(publicKey));
    }

    [TearDown]
    public void TearDown() => _rsa.Dispose();

    private static PluginManifestVerifier CreateVerifier(bool devModeActive, ILogger<PluginManifestVerifier>? logger = null)
    {
        var devMode = new Mock<IDeveloperModeProvider>();
        devMode.SetupGet(d => d.IsActive).Returns(devModeActive);
        return new PluginManifestVerifier(devMode.Object, logger ?? Mock.Of<ILogger<PluginManifestVerifier>>());
    }

    [Test]
    public void Verify_ValidHashAndSignature_ReturnsTrue()
    {
        var sut = CreateVerifier(devModeActive: false);

        var result = sut.Verify(_validManifest, _assemblyBytes);

        Assert.That(result, Is.True);
    }

    [Test]
    public void Verify_TamperedAssemblyBytes_ReturnsFalse()
    {
        var tampered = (byte[])_assemblyBytes.Clone();
        tampered[0] ^= 0xFF;

        var sut = CreateVerifier(devModeActive: false);

        var result = sut.Verify(_validManifest, tampered);

        Assert.That(result, Is.False);
    }

    [Test]
    public void Verify_CorrectHashButInvalidSignature_ReturnsFalse()
    {
        using var otherKey = RSA.Create(2048);
        var hash = SHA256.HashData(_assemblyBytes);
        var wrongSignature = otherKey.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        var manifest = _validManifest with { SignatureBase64 = Convert.ToBase64String(wrongSignature) };

        var sut = CreateVerifier(devModeActive: false);

        var result = sut.Verify(manifest, _assemblyBytes);

        Assert.That(result, Is.False);
    }

    [Test]
    public void Verify_DeveloperModeActive_ReturnsTrueAndLogsWarning()
    {
        var invalidManifest = new PluginManifest("dev-provider", "aA==", "cw==", "cA==");
        var loggerMock = new Mock<ILogger<PluginManifestVerifier>>();

        var sut = CreateVerifier(devModeActive: true, loggerMock.Object);

        var result = sut.Verify(invalidManifest, _assemblyBytes);

        Assert.That(result, Is.True);
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("dev-provider")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
