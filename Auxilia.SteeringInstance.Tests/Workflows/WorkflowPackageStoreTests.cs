using Auxilia.PlatformData.Entities;
using Auxilia.SteeringInstance.Workflows.Storage;
using Auxilia.UniversalDataAccess.Implementations;

namespace Auxilia.SteeringInstance.Tests.Workflows;

[TestFixture]
[Category("Unit")]
public class WorkflowPackageStoreTests
{
    private InMemoryDataAccess<WorkflowPackageRecord> _records = null!;
    private WorkflowPackageStore _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _records = new InMemoryDataAccess<WorkflowPackageRecord>();
        _sut = new WorkflowPackageStore(_records, TimeProvider.System);
    }

    [TearDown]
    public void TearDown() => _records.Dispose();

    [Test]
    public async Task Register_StoresSeededRecordWithMetadata()
    {
        await _sut.RegisterAsync("code-review", "docker://review:1", "Code review", "2.0");

        var record = await _sut.GetAsync("code-review");
        Assert.Multiple(() =>
        {
            Assert.That(record!.PackageUri, Is.EqualTo("docker://review:1"));
            Assert.That(record.DisplayName, Is.EqualTo("Code review"));
            Assert.That(record.Version, Is.EqualTo("2.0"));
            Assert.That(record.Source, Is.EqualTo("seed"));
        });
    }

    [Test]
    public async Task Register_WithoutDisplayName_FallsBackToWorkflowType()
    {
        await _sut.RegisterAsync("code-review", "docker://review:1", null, null);

        var record = await _sut.GetAsync("code-review");
        Assert.That(record!.DisplayName, Is.EqualTo("code-review"));
    }

    [Test]
    public async Task Learn_CreatesMissingEntryFromDispatch()
    {
        await _sut.LearnAsync("ad-hoc", "docker://adhoc:latest");

        var record = await _sut.GetAsync("ad-hoc");
        Assert.Multiple(() =>
        {
            Assert.That(record!.PackageUri, Is.EqualTo("docker://adhoc:latest"));
            Assert.That(record.Source, Is.EqualTo("run"));
            Assert.That(record.DisplayName, Is.EqualTo("ad-hoc"));
        });
    }

    [Test]
    public async Task Learn_NeverOverwritesASeededRegistration()
    {
        await _sut.RegisterAsync("code-review", "docker://review:1", "Code review", "2.0");

        await _sut.LearnAsync("code-review", "docker://review:experimental");

        var record = await _sut.GetAsync("code-review");
        Assert.Multiple(() =>
        {
            Assert.That(record!.PackageUri, Is.EqualTo("docker://review:1"));
            Assert.That(record.Source, Is.EqualTo("seed"));
        });
    }

    [Test]
    public async Task Learn_UpdatesUriOfAnEarlierLearnedEntry()
    {
        await _sut.LearnAsync("ad-hoc", "docker://adhoc:1");
        await _sut.LearnAsync("ad-hoc", "docker://adhoc:2");

        var record = await _sut.GetAsync("ad-hoc");
        Assert.That(record!.PackageUri, Is.EqualTo("docker://adhoc:2"));
    }

    [Test]
    public async Task Learn_IgnoresBlankCoordinates()
    {
        await _sut.LearnAsync("", "docker://x");
        await _sut.LearnAsync("type", "");

        Assert.That(await _sut.GetAllAsync(), Is.Empty);
    }

    [Test]
    public async Task Remove_DeletesTheEntry()
    {
        await _sut.RegisterAsync("code-review", "docker://review:1", null, null);

        Assert.That(await _sut.RemoveAsync("code-review"), Is.True);
        Assert.That(await _sut.GetAsync("code-review"), Is.Null);
    }
}
