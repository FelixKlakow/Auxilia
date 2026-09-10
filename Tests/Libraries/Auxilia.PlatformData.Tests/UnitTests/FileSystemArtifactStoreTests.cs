using System.Security.Cryptography;
using System.Text;
using Auxilia.PlatformData.Artifacts;
using Auxilia.PlatformData.Entities;
using Auxilia.UniversalDataAccess;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.PlatformData.Tests.UnitTests;

[TestFixture]
[Category("Unit")]
public class FileSystemArtifactStoreTests
{
    private string _dir = null!;
    private string PayloadRoot => Path.Combine(_dir, "Artifacts");
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

    [Test]
    public async Task Save_LeavesOnlyTheFinalPayloadFile()
    {
        var record = await SaveAsync("payload");

        var files = Directory.GetFiles(PayloadRoot).Select(Path.GetFileName).ToList();
        Assert.That(files, Is.EqualTo(new[] { record.Id.ToString("N") }),
            "The pending file must be moved to its final name, not copied or left behind.");
    }

    [Test]
    public void Save_IndexWriteFails_SurfacesAndLeavesNoPayloadOnDisk()
    {
        var sut = new FileSystemArtifactStore(
            new FailingWriteIndex(_index), TimeProvider.System, new PlatformDataSettings { JsonDirectory = _dir },
            new ArtifactStoreSettings());

        using var payload = new MemoryStream(Encoding.UTF8.GetBytes("orphan?"));
        Assert.ThrowsAsync<IOException>(
            () => sut.SaveAsync("review-result", "review-workflow", "WI-1", Guid.NewGuid(), payload));

        Assert.That(Directory.GetFiles(PayloadRoot), Is.Empty, "A failed save must not orphan a payload file.");
    }

    [Test]
    public async Task Save_ConcurrentSavesInOneLineage_AssignDistinctSequentialVersions()
    {
        const int writers = 16;
        var saves = Enumerable.Range(1, writers).Select(i => SaveAsync($"v{i}")).ToList();

        var records = await Task.WhenAll(saves);

        Assert.That(records.Select(r => r.Version).OrderBy(v => v), Is.EqualTo(Enumerable.Range(1, writers)),
            "Versions inside one lineage are assigned under a per-lineage gate, never duplicated.");
        Assert.That((await _sut.ResolveLatestAsync("review-result", "WI-1"))!.Version, Is.EqualTo(writers));
    }

    /// <summary>An index whose writes fail — reads pass through to the real in-memory index.</summary>
    private sealed class FailingWriteIndex(IDataAccess<ArtifactRecord> inner) : IDataAccess<ArtifactRecord>
    {
        public IObservable<ArtifactRecord> EntityAdded => inner.EntityAdded;
        public IObservable<ArtifactRecord> EntityUpdated => inner.EntityUpdated;
        public IObservable<ArtifactRecord> EntityRemoved => inner.EntityRemoved;
        public Task<IQueryable<ArtifactRecord>> ReadAsync(CancellationToken cancellationToken) => inner.ReadAsync(cancellationToken);
        public Task<ArtifactRecord?> ReadAsync(Guid id, CancellationToken cancellationToken) => inner.ReadAsync(id, cancellationToken);
        public Task<bool> SaveAsync(ArtifactRecord entity, CancellationToken cancellationToken) => throw new IOException("index unavailable");
        public Task<bool> TrySaveAsync(ArtifactRecord entity, long expectedVersion, CancellationToken cancellationToken) => throw new IOException("index unavailable");
        public Task<bool> RemoveAsync(Guid id) => inner.RemoveAsync(id);
        public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken) => inner.RemoveAsync(id, cancellationToken);
    }
}
