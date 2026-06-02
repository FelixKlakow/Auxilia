using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auxilia.Workflows.Crypto;
using Microsoft.Extensions.Logging;
using Moq;

namespace Auxilia.Workflows.Tests.Crypto;

[TestFixture]
[Category("Unit")]
public class WorkflowPackageVerifierTests
{
    private RSA _rsa = null!;
    private MemoryStream _validZipStream = null!;

    [SetUp]
    public void SetUp()
    {
        _rsa = RSA.Create(2048);

        var schemaContent = Encoding.UTF8.GetBytes("{}");
        var schemaHash = SHA256.HashData(schemaContent);
        var fileEntry = new WorkflowPackageFileEntry(
            FileName: "workflow-schema.json",
            HashBase64: Convert.ToBase64String(schemaHash));

        var publicKeyBase64 = Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());

        var unsignedManifest = new WorkflowPackageManifest(
            Files: [fileEntry],
            SignatureBase64: string.Empty,
            PublicKeyBase64: publicKeyBase64,
            ExecutableRelativePath: "workflow.dll");

        var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsignedManifest, WorkflowPackageJsonOptions.SerializeOptions);
        var manifestHash = SHA256.HashData(unsignedBytes);
        var signature = _rsa.SignHash(manifestHash, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

        var signedManifest = unsignedManifest with { SignatureBase64 = Convert.ToBase64String(signature) };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(signedManifest, WorkflowPackageJsonOptions.SerializeOptions);

        _validZipStream = BuildZip([("workflow-schema.json", schemaContent), ("package-manifest.json", manifestBytes)]);
    }

    [TearDown]
    public void TearDown()
    {
        _rsa.Dispose();
        _validZipStream.Dispose();
    }

    private static WorkflowPackageVerifier CreateVerifier(bool devModeActive, ILogger<WorkflowPackageVerifier>? logger = null)
    {
        var devMode = new Mock<IDeveloperModeProvider>();
        devMode.SetupGet(d => d.IsActive).Returns(devModeActive);
        return new WorkflowPackageVerifier(devMode.Object, logger ?? Mock.Of<ILogger<WorkflowPackageVerifier>>());
    }

    private static MemoryStream BuildZip(IEnumerable<(string name, byte[] content)> entries)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    [Test]
    public void Verify_ValidZip_ReturnsTrue()
    {
        _validZipStream.Position = 0;
        var sut = CreateVerifier(devModeActive: false);

        var result = sut.Verify(_validZipStream);

        Assert.That(result, Is.True);
    }

    [Test]
    public void Verify_TamperedFileContent_ReturnsFalse()
    {
        // Build ZIP with correct hash in manifest but wrong file bytes
        var schemaContent = Encoding.UTF8.GetBytes("{}");
        var correctHash = SHA256.HashData(schemaContent);
        var fileEntry = new WorkflowPackageFileEntry(
            FileName: "workflow-schema.json",
            HashBase64: Convert.ToBase64String(correctHash));

        var publicKeyBase64 = Convert.ToBase64String(_rsa.ExportSubjectPublicKeyInfo());
        var unsignedManifest = new WorkflowPackageManifest(
            Files: [fileEntry],
            SignatureBase64: string.Empty,
            PublicKeyBase64: publicKeyBase64,
            ExecutableRelativePath: "workflow.dll");

        var unsignedBytes = JsonSerializer.SerializeToUtf8Bytes(unsignedManifest, WorkflowPackageJsonOptions.SerializeOptions);
        var manifestHash = SHA256.HashData(unsignedBytes);
        var signature = _rsa.SignHash(manifestHash, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var signedManifest = unsignedManifest with { SignatureBase64 = Convert.ToBase64String(signature) };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(signedManifest, WorkflowPackageJsonOptions.SerializeOptions);

        var tamperedContent = Encoding.UTF8.GetBytes("tampered content");
        using var zipStream = BuildZip([("workflow-schema.json", tamperedContent), ("package-manifest.json", manifestBytes)]);

        var sut = CreateVerifier(devModeActive: false);
        var result = sut.Verify(zipStream);

        Assert.That(result, Is.False);
    }

    [Test]
    public void Verify_TamperedSignature_ReturnsFalse()
    {
        var randomSignature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(256));

        // Read valid manifest from _validZipStream and replace signature
        _validZipStream.Position = 0;
        WorkflowPackageManifest originalManifest;
        byte[] schemaContent;
        using (var archive = new ZipArchive(_validZipStream, ZipArchiveMode.Read, leaveOpen: true))
        {
            var manifestEntry = archive.GetEntry("package-manifest.json")!;
            using var ms = new MemoryStream();
            manifestEntry.Open().CopyTo(ms);
            originalManifest = JsonSerializer.Deserialize<WorkflowPackageManifest>(ms.ToArray(), WorkflowPackageVerifier.DeserializeOptions)!;

            var schemaEntry = archive.GetEntry("workflow-schema.json")!;
            using var schemaMem = new MemoryStream();
            schemaEntry.Open().CopyTo(schemaMem);
            schemaContent = schemaMem.ToArray();
        }

        var tamperedManifest = originalManifest with { SignatureBase64 = randomSignature };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(tamperedManifest, WorkflowPackageJsonOptions.SerializeOptions);

        using var zipStream = BuildZip([("workflow-schema.json", schemaContent), ("package-manifest.json", manifestBytes)]);

        var sut = CreateVerifier(devModeActive: false);
        var result = sut.Verify(zipStream);

        Assert.That(result, Is.False);
    }

    [Test]
    public void Verify_DeveloperModeActive_ReturnsTrueWithWarningLog()
    {
        var devMode = new Mock<IDeveloperModeProvider>();
        devMode.Setup(d => d.IsActive).Returns(true);
        var loggerMock = new Mock<ILogger<WorkflowPackageVerifier>>();

        var sut = new WorkflowPackageVerifier(devMode.Object, loggerMock.Object);
        using var emptyStream = new MemoryStream();

        var result = sut.Verify(emptyStream);

        Assert.That(result, Is.True);
        loggerMock.Verify(
            l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("developer mode")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
