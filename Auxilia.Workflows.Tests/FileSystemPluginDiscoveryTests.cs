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
    public void Discover_MatchingPair_ReturnsOnePlugin()
    {
        WriteDll("plugin-a");
        WriteManifest("plugin-a", "provider-a");

        var result = _sut.DiscoverPlugins(_tempDir);

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].ProviderType, Is.EqualTo("provider-a"));
        Assert.That(result[0].AssemblyPath,
            Is.EqualTo(Path.Combine(_tempDir, "plugin-a.slothandler.dll")));
    }

    [Test]
    public void Discover_DllWithoutSidecar_ReturnsEmpty()
    {
        WriteDll("no-manifest");

        var result = _sut.DiscoverPlugins(_tempDir);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Discover_EmptyDirectory_ReturnsEmpty()
    {
        var result = _sut.DiscoverPlugins(_tempDir);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public void Discover_JsonOnlyNodll_ReturnsEmpty()
    {
        WriteManifest("json-only", "provider-x");

        var result = _sut.DiscoverPlugins(_tempDir);

        Assert.That(result, Is.Empty);
    }
}
