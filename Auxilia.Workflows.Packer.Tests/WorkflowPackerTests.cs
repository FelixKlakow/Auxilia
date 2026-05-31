using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Auxilia.Workflows;
using Auxilia.Workflows.Crypto;
using Auxilia.Workflows.Environment;
using Auxilia.Workflows.Packer;

namespace Auxilia.Workflows.Packer.Tests;

[TestFixture]
public sealed class WorkflowPackerTests : IDisposable
{
    private string _inputDir = null!;
    private string _outputPath = null!;
    private string _pemFilePath = null!;
    private RSA _rsa = null!;
    private RsaFileSigningProvider _signer = null!;
    private WorkflowPacker _packer = null!;
    private string _executableName = null!;
    private static readonly byte[] SampleJsonContent = """{"key":"value"}"""u8.ToArray();

    [SetUp]
    public void SetUp()
    {
        _inputDir = Path.Combine(Path.GetTempPath(), $"packer-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(Path.Combine(_inputDir, "data"));

        _executableName = OperatingSystem.IsWindows() ? "stub.exe" : "stub";
        File.WriteAllBytes(Path.Combine(_inputDir, _executableName), [0x00]);
        File.WriteAllBytes(Path.Combine(_inputDir, "data", "sample.json"), SampleJsonContent);

        _rsa = RSA.Create(2048);
        _pemFilePath = Path.GetTempFileName();
        File.WriteAllText(_pemFilePath, _rsa.ExportRSAPrivateKeyPem());

        _signer = new RsaFileSigningProvider(_pemFilePath);

        var schema = new WorkflowSchema("test-workflow", [], []);
        _packer = new WorkflowPacker(_signer, _ => schema);

        _outputPath = Path.Combine(Path.GetTempPath(), $"output-{Guid.NewGuid():N}.workflow.zip");
    }

    [TearDown]
    public void TearDown()
    {
        _signer.Dispose();
        _rsa.Dispose();
        if (File.Exists(_pemFilePath)) File.Delete(_pemFilePath);
        if (File.Exists(_outputPath)) File.Delete(_outputPath);
        if (Directory.Exists(_inputDir)) Directory.Delete(_inputDir, recursive: true);
    }

    [Test]
    public void Pack_CreatesZipWithExpectedEntries()
    {
        _packer.Pack(_inputDir, _outputPath);

        using var zip = ZipFile.OpenRead(_outputPath);
        var entryNames = zip.Entries.Select(e => e.FullName).ToList();

        Assert.That(entryNames, Does.Contain(_executableName));
        Assert.That(entryNames, Does.Contain("data/sample.json"));
        Assert.That(entryNames, Does.Contain("workflow-schema.json"));
        Assert.That(entryNames, Does.Contain("package-manifest.json"));
    }

    [Test]
    public void Pack_ManifestFilesExcludesPackageManifestJson()
    {
        _packer.Pack(_inputDir, _outputPath);

        var manifest = ReadManifest(_outputPath);
        Assert.That(manifest.Files.Select(f => f.FileName), Does.Not.Contain("package-manifest.json"));
    }

    [Test]
    public void Pack_ManifestSignatureIsNonEmpty()
    {
        _packer.Pack(_inputDir, _outputPath);

        var manifest = ReadManifest(_outputPath);
        Assert.That(manifest.SignatureBase64.Length, Is.GreaterThan(0));
    }

    [Test]
    public void Pack_ManifestPublicKeyMatchesProvider()
    {
        _packer.Pack(_inputDir, _outputPath);

        var manifest = ReadManifest(_outputPath);
        Assert.That(manifest.PublicKeyBase64, Is.EqualTo(_signer.PublicKeyBase64));
    }

    [Test]
    public void Pack_FileHashMatchesContent()
    {
        _packer.Pack(_inputDir, _outputPath);

        var manifest = ReadManifest(_outputPath);
        var entry = manifest.Files.Single(f => f.FileName == "data/sample.json");

        var expectedHash = Convert.ToBase64String(SHA256.HashData(SampleJsonContent));
        Assert.That(entry.HashBase64, Is.EqualTo(expectedHash));
    }

    [Test]
    public void Pack_ThrowsWhenNoExecutableFound()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);

        try
        {
            Assert.Throws<InvalidOperationException>(() => _packer.Pack(emptyDir, _outputPath));
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    private static WorkflowPackageManifest ReadManifest(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("package-manifest.json")!;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return JsonSerializer.Deserialize<WorkflowPackageManifest>(ms.ToArray(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    public void Dispose()
    {
        _signer.Dispose();
        _rsa.Dispose();
    }
}
