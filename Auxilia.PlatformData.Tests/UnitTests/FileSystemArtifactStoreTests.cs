using System.Security.Cryptography;
using System.Text;
using Auxilia.PlatformData.Artifacts;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.PlatformData.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class FileSystemArtifactStoreTests
{
    private string _dir = null!;
    private InMemoryDataAccess<ArtifactRecord> _index = null!;
    private FileSystemArtifactStore _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"auxilia-artifact-store-{Guid.NewGuid():N}");
        _index = new InMemoryDataAccess<ArtifactRecord>();
        _sut = new FileSystemArtifactStore(
            _index, TimeProvider.System, new PlatformDataSettings { JsonDirectory = _dir },
            new ArtifactStoreSettings());
    }

    [TearDown]
    public void TearDown()
    {
        _index.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private async Task<ArtifactRecord> SaveAsync(string content, string artifactType = "review-result", string workItemId = "WI-1")
    {
        using var payload = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await _sut.SaveAsync(artifactType, "review-workflow", workItemId, Guid.NewGuid(), payload);
    }

    [Test]
    public async Task Save_ComputesSha256HashAndSizeOfPayload()
    {
        var payloadBytes = Encoding.UTF8.GetBytes("""{"verdict":"approve"}""");

        var record = await SaveAsync("""{"verdict":"approve"}""");

        Assert.That(record.ContentHash, Is.EqualTo(Convert.ToHexString(SHA256.HashData(payloadBytes))));
        Assert.That(record.SizeBytes, Is.EqualTo(payloadBytes.Length));
    }

    [Test]
    public async Task OpenRead_RoundTripsPayloadContent()
    {
        var record = await SaveAsync("artifact payload content");

        await using var stream = await _sut.OpenReadAsync(record.Id);
        Assert.That(stream, Is.Not.Null);
        using var reader = new StreamReader(stream!);
        Assert.That(await reader.ReadToEndAsync(), Is.EqualTo("artifact payload content"));
    }

    [Test]
    public async Task Save_SameLineage_AssignsIncrementingVersions()
    {
        var first = await SaveAsync("v1");
        var second = await SaveAsync("v2");

        Assert.That(first.Version, Is.EqualTo(1));
        Assert.That(second.Version, Is.EqualTo(2));
    }

    [Test]
    public async Task Save_DifferentWorkItem_StartsItsOwnLineage()
    {
        await SaveAsync("v1", workItemId: "WI-1");
        var other = await SaveAsync("v1 of other", workItemId: "WI-2");

        Assert.That(other.Version, Is.EqualTo(1));
    }

    [Test]
    public async Task ResolveLatest_ReturnsNewestVersion()
    {
        await SaveAsync("v1");
        var second = await SaveAsync("v2");

        var latest = await _sut.ResolveLatestAsync("review-result", "WI-1");

        Assert.That(latest, Is.Not.Null);
        Assert.That(latest!.Id, Is.EqualTo(second.Id));
        Assert.That(latest.Version, Is.EqualTo(2));
    }

    [Test]
    public async Task ResolveLatest_UnknownLineage_ReturnsNull()
    {
        Assert.That(await _sut.ResolveLatestAsync("review-result", "WI-404"), Is.Null);
    }

    [Test]
    public async Task GetLineage_ReturnsAllVersionsOldestFirst()
    {
        var first = await SaveAsync("v1");
        var second = await SaveAsync("v2");
        var third = await SaveAsync("v3");
        await SaveAsync("other lineage", workItemId: "WI-2");

        var lineage = await _sut.GetLineageAsync("review-result", "WI-1");

        Assert.That(lineage.Select(r => r.Id), Is.EqualTo(new[] { first.Id, second.Id, third.Id }));
        Assert.That(lineage.Select(r => r.Version), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task OpenRead_UnknownId_ReturnsNull()
    {
        Assert.That(await _sut.OpenReadAsync(Guid.NewGuid()), Is.Null);
    }

    [Test]
    public async Task Get_ReturnsIndexedRecord()
    {
        var record = await SaveAsync("payload");

        Assert.That(await _sut.GetAsync(record.Id), Is.EqualTo(record));
    }
}
