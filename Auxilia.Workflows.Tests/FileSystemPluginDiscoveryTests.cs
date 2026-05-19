using System.Text.Json;

namespace Auxilia.Workflows.Tests;

[TestFixture]
[Category("Unit")]
public class FileSystemPluginDiscoveryTests
{
    private string _tempDir = null!;
    private FileSystemPluginDiscovery _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
        _sut = new FileSystemPluginDiscovery();
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteDll(string baseName)
        => File.WriteAllBytes(Path.Combine(_tempDir, $"{baseName}.slothandler.dll"), []);

    private void WriteManifest(string baseName, string providerType)
    {
        var manifest = new PluginManifest(providerType, "aGFzaA==", "c2ln", "cHVi");
        var json = JsonSerializer.Serialize(manifest);
        File.WriteAllText(Path.Combine(_tempDir, $"{baseName}.slothandler.manifest.json"), json);
    }

    [Test]
    public void DiscoverPlugins_ReturnsBothEntries_WhenDllAndManifestExist()
    {
        WriteDll("plugin-a");
        WriteManifest("plugin-a", "provider-a");
        WriteDll("plugin-b");
        WriteManifest("plugin-b", "provider-b");

        var result = _sut.DiscoverPlugins(_tempDir);

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Select(p => p.ProviderType), Does.Contain("provider-a"));
        Assert.That(result.Select(p => p.ProviderType), Does.Contain("provider-b"));
        Assert.That(result.Select(p => p.AssemblyPath),
            Does.Contain(Path.Combine(_tempDir, "plugin-a.slothandler.dll")));
    }

    [Test]
    public void DiscoverPlugins_IgnoresDll_WhenManifestSidecarMissing()
    {
        WriteDll("with-manifest");
        WriteManifest("with-manifest", "paired-provider");
        WriteDll("no-manifest");

        var result = _sut.DiscoverPlugins(_tempDir);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ProviderType, Is.EqualTo("paired-provider"));
    }

    [Test]
    public void DiscoverPlugins_ReturnsEmpty_ForEmptyDirectory()
    {
        var result = _sut.DiscoverPlugins(_tempDir);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void DiscoverPlugins_ReturnsEmpty_WhenDirectoryDoesNotExist()
    {
        var nonExistent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        var result = _sut.DiscoverPlugins(nonExistent);

        Assert.That(result, Is.Empty);
    }
}
